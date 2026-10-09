package claude

import (
	"context"
	"errors"
	"fmt"
	"iter"
	"net/http"
	"time"

	"github.com/anthropics/anthropic-sdk-go"
	"github.com/anthropics/anthropic-sdk-go/option"

	"github.com/sleepyshark85/officina/go/officina"
)

// Opus55 is Claude Opus 5.5's model identifier.
const Opus55 = string(anthropic.ModelClaudeOpus5_5)

// Effort is how hard Claude thinks and how much it spends. It is always set, never left to the model's default.
type Effort string

// The efforts, as the API names them.
const (
	EffortLow    Effort = "low"
	EffortMedium Effort = "medium"
	EffortHigh   Effort = "high"
	EffortXHigh  Effort = "xhigh"
	EffortMax    Effort = "max"
)

// CacheTTL is how long Claude keeps a cache entry.
type CacheTTL string

// The cache lifetimes, as the API names them.
const (
	CacheFiveMinutes CacheTTL = "5m"
	// CacheOneHour is for reads that come further apart: a prefix shared by many conversations, users who reply
	// slowly. Its writes cost more.
	CacheOneHour CacheTTL = "1h"
)

// Options holds a Model's optional settings; the zero value is the defaults.
type Options struct {
	// MaxOutputTokens is the longest reply, in tokens; 64,000 when zero. Every request streams, so a long reply does
	// not time out.
	MaxOutputTokens int64
	// PrefixCache is the lifetime of the cached prefix (tools and instructions); five minutes when empty. It may not
	// be shorter than ConversationCache, as the API requires longer-lived entries first.
	PrefixCache CacheTTL
	// ConversationCache is the lifetime of the cached conversation, which only its next call reads; five minutes
	// when empty.
	ConversationCache CacheTTL
	// APIKey is the API key; when empty, the SDK finds credentials as usual (ANTHROPIC_API_KEY).
	APIKey string
	// BaseURL is the API's address; when empty, the SDK's default or ANTHROPIC_BASE_URL.
	BaseURL string
	// HTTPClient sends the requests; http.DefaultClient when nil.
	HTTPClient *http.Client
}

// Model is Claude with fixed settings, an officina.Model. Many runs may stream from one Model at once.
type Model struct {
	client       anthropic.Client
	name         string
	effort       Effort
	maxTokens    int64
	prefix, tail CacheTTL
}

// New returns the Claude model named name (such as Opus55) with the given effort. It fails if the name is blank,
// the effort or a cache lifetime is unknown, or the prefix's cache lifetime is shorter than the conversation's.
func New(name string, effort Effort, opts Options) (*Model, error) {
	m := &Model{
		name: name, effort: effort, maxTokens: opts.MaxOutputTokens,
		prefix: opts.PrefixCache, tail: opts.ConversationCache,
	}
	if m.maxTokens == 0 {
		m.maxTokens = 64_000
	}
	if m.prefix == "" {
		m.prefix = CacheFiveMinutes
	}
	if m.tail == "" {
		m.tail = CacheFiveMinutes
	}
	switch {
	case name == "":
		return nil, errors.New("new claude model: no model name")
	case effort != EffortLow && effort != EffortMedium && effort != EffortHigh && effort != EffortXHigh &&
		effort != EffortMax:
		return nil, fmt.Errorf("new claude model: unknown effort %q", effort)
	case m.maxTokens < 1:
		return nil, fmt.Errorf("new claude model: max output tokens %d is not positive", m.maxTokens)
	case !knownTTL(m.prefix) || !knownTTL(m.tail):
		return nil, fmt.Errorf("new claude model: unknown cache lifetime %q or %q", m.prefix, m.tail)
	case m.prefix == CacheFiveMinutes && m.tail == CacheOneHour:
		return nil, errors.New("new claude model: the prefix's cache lifetime is shorter than the conversation's; " +
			"the API requires longer-lived cache entries first")
	}
	// Retries are this package's own: they honour Retry-After mid-stream too, and the run is told of each.
	clientOpts := []option.RequestOption{option.WithMaxRetries(0)}
	if opts.APIKey != "" {
		clientOpts = append(clientOpts, option.WithAPIKey(opts.APIKey))
	}
	if opts.BaseURL != "" {
		clientOpts = append(clientOpts, option.WithBaseURL(opts.BaseURL))
	}
	if opts.HTTPClient != nil {
		clientOpts = append(clientOpts, option.WithHTTPClient(opts.HTTPClient))
	}
	m.client = anthropic.NewClient(clientOpts...)
	return m, nil
}

func knownTTL(ttl CacheTTL) bool {
	return ttl == CacheFiveMinutes || ttl == CacheOneHour
}

// Settings returns every setting that shapes the model's requests, in the .NET implementation's words, such as
// "claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=5m thinking=adaptive". The cache lifetimes
// are one word when they are the same, else the prefix's then the conversation's ("1h/5m").
func (m *Model) Settings() string {
	cache := string(m.prefix)
	if m.prefix != m.tail {
		cache += "/" + string(m.tail)
	}
	return fmt.Sprintf("claude model=%s effort=%s max_tokens=%d cache=%s thinking=adaptive",
		m.name, m.effort, m.maxTokens, cache)
}

// The retry policy: attempts per call, the first included, and the bounds of the wait between them.
const (
	maxAttempts  = 5
	firstBackoff = time.Second
	longestWait  = 30 * time.Second
)

// Stream sends req as one streamed request and yields the reply, as officina.Model says. Each block is yielded as
// soon as it is complete. A transient failure, also one mid-stream, is retried after a Retried event; the usage the
// failed attempt reported stays counted. A prompt longer than the context window finishes as
// officina.FinishContextFull.
func (m *Model) Stream(ctx context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error] {
	return func(yield func(officina.ModelEvent, error) bool) {
		params, err := m.params(req)
		if err != nil {
			yield(nil, err)
			return
		}
		for attempt := 1; ; attempt++ {
			stopped, err := m.attempt(ctx, params, yield)
			switch {
			case stopped || err == nil:
				return
			case ctx.Err() != nil:
				yield(nil, ctx.Err())
				return
			case promptTooLong(err):
				yield(officina.Finished{Reason: officina.FinishContextFull}, nil)
				return
			case !transient(err):
				yield(nil, classify(err))
				return
			case attempt == maxAttempts:
				yield(nil, fmt.Errorf("%w, after %d attempts: %w", ErrTransient, attempt, err))
				return
			}
			if !yield(officina.Retried{}, nil) {
				return
			}
			timer := time.NewTimer(backoff(attempt, retryAfter(err)))
			select {
			case <-ctx.Done():
				timer.Stop()
				yield(nil, ctx.Err())
				return
			case <-timer.C:
			}
		}
	}
}
