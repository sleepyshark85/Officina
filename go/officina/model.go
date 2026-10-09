package officina

import (
	"context"
	"iter"
)

// Model is a provider's model with fixed settings: the only way the core reaches a model. Many runs may stream from
// one Model at once.
type Model interface {
	// Settings returns the model and every setting that shapes its requests, as text that changes when any of them
	// does. It is part of the prefix fingerprint, so it must not change during the Model's life.
	Settings() string
	// Info names the model for telemetry and gives its price. It must not change during the Model's life.
	Info() ModelInfo
	// Stream sends one request and yields the reply: text deltas and complete blocks as they arrive, usage, and last
	// a Finished. The Model retries transient failures itself, yielding a Retried before each retry; a failure that
	// remains is yielded as an error, which ends the reply. When ctx is done or the caller stops early, Stream
	// releases everything it started.
	//
	// A caller that stops early does so by yield returning false; ctx is cancelled only after the stream has
	// returned. So a stream that waits for a goroutine of its own must first stop it on that signal too, not only
	// on ctx, or it waits forever.
	Stream(ctx context.Context, req Request) iter.Seq2[ModelEvent, error]
}

// Request is one model call: the prefix that stays the same for a conversation (tools and instructions; the model's
// settings are its own), then the conversation with the run's pending messages. A Model must not modify it.
type Request struct {
	// Tools are sorted by name.
	Tools        []Tool
	Instructions string
	Messages     []Message
}

// Usage counts tokens as the provider bills them.
type Usage struct {
	// Input counts the input tokens neither read from nor written to the cache.
	Input      int64 `json:"input"`
	Output     int64 `json:"output"`
	CacheRead  int64 `json:"cacheRead"`
	CacheWrite int64 `json:"cacheWrite"`
	// CacheWriteHour counts the cache writes kept for an hour, which cost more: part of CacheWrite.
	CacheWriteHour int64 `json:"cacheWriteHour"`
}

func (u Usage) plus(v Usage) Usage {
	return Usage{
		u.Input + v.Input, u.Output + v.Output, u.CacheRead + v.CacheRead, u.CacheWrite + v.CacheWrite,
		u.CacheWriteHour + v.CacheWriteHour,
	}
}

// ModelInfo names a model as telemetry does, and gives its price.
type ModelInfo struct {
	// Provider is the provider's name (gen_ai.provider.name), such as "anthropic".
	Provider string
	// Name is the model's identifier (gen_ai.request.model).
	Name  string
	Price Price
}

// Price is what a model's tokens cost, in US dollars per million tokens. The zero value is a price not known: the
// tokens then cost nothing.
type Price struct {
	// Input is for the input tokens neither read from nor written to the cache.
	Input     float64
	Output    float64
	CacheRead float64
	// CacheWrite is for the input tokens cached for the short default time, CacheWriteHour for those cached for an
	// hour.
	CacheWrite, CacheWriteHour float64
}

// cost returns what u costs, in US dollars.
func (p Price) cost(u Usage) float64 {
	return (float64(u.Input)*p.Input + float64(u.Output)*p.Output + float64(u.CacheRead)*p.CacheRead +
		float64(u.CacheWrite-u.CacheWriteHour)*p.CacheWrite + float64(u.CacheWriteHour)*p.CacheWriteHour) / 1e6
}

// ModelEvent is something a model streams while it replies: a TextDelta, BlockReceived, UsageReceived, Retried or
// Finished.
type ModelEvent interface {
	modelEvent()
}

// TextDelta is a piece of reply text, for display as it streams; the complete block follows in a BlockReceived.
type TextDelta struct {
	Text string
}

// BlockReceived is a complete block of the reply, in reply order.
type BlockReceived struct {
	Block Block
}

// UsageReceived reports the tokens used since the call's previous report: reports are increments.
type UsageReceived struct {
	Usage Usage
}

// Retried says the call failed and is made again: the text and blocks streamed before it belong to a reply that
// will not come. The usage reported before it stays counted, as it is billed.
type Retried struct{}

// Finished says why the model stopped: the reply's last event.
type Finished struct {
	Reason FinishReason
	// Detail is a refusal's category, or the provider's own word for an unknown reason.
	Detail string
}

func (TextDelta) modelEvent()     {}
func (BlockReceived) modelEvent() {}
func (UsageReceived) modelEvent() {}
func (Retried) modelEvent()       {}
func (Finished) modelEvent()      {}

// FinishReason is why a model stopped, in the model contract's words.
type FinishReason int

// The reasons a model stops. The zero value is FinishUnknown.
const (
	// FinishUnknown is a reason the provider gave that is none of the others.
	FinishUnknown FinishReason = iota
	FinishEnd
	FinishToolUse
	FinishMaxTokens
	FinishRefusal
	FinishContextFull
)

// String returns the reason's name.
func (r FinishReason) String() string {
	if r == FinishUnknown {
		return "FinishUnknown"
	}
	return name(int(r), "FinishReason", "FinishEnd", "FinishToolUse", "FinishMaxTokens", "FinishRefusal",
		"FinishContextFull")
}
