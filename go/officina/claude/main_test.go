package claude_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"
	"github.com/google/go-cmp/cmp/cmpopts"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

func TestMain(m *testing.M) {
	// The SDK's JSON encoder builds each type's encoder once and makes later callers wait for it on a WaitGroup. If
	// a test in a synctest bubble (the retry tests) built one first, a parallel test outside the bubble would wait on
	// a WaitGroup tied to the bubble, and panic. Building every encoder the tests use here, outside any bubble,
	// avoids it.
	if err := warmEncoders(); err != nil {
		fmt.Fprintln(os.Stderr, "warm the SDK's encoders:", err)
		os.Exit(1)
	}
	goleak.VerifyTestMain(m)
}

// warmEncoders streams, outside any synctest bubble, one request of every shape the tests send, and decodes a
// reply of every kind of block and an API error.
func warmEncoders() error {
	tool, err := os.ReadFile(shared("claude", "thinking-text-tool.sse"))
	if err != nil {
		return err
	}
	replies := []string{string(tool), ""}
	var mu sync.Mutex
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = io.Copy(io.Discard, r.Body) // Only the request's encoding matters.
		mu.Lock()
		reply := replies[0]
		replies = replies[1:]
		mu.Unlock()
		if reply == "" {
			apiError(http.StatusBadRequest, "invalid_request_error", "warm", "")(w)
			return
		}
		sse(reply)(w)
	}))
	defer srv.Close()
	m, err := claude.New(claude.Opus55, claude.EffortHigh, claude.Options{
		APIKey: "test-key", BaseURL: srv.URL, MaxOutputTokens: 1000, PrefixCache: claude.CacheOneHour,
		ConversationCache: claude.CacheFiveMinutes,
		HTTPClient:        &http.Client{Transport: &http.Transport{DisableKeepAlives: true}},
	})
	if err != nil {
		return err
	}
	req := officina.Request{
		Tools:        []officina.Tool{{Name: "search", Description: "Searches.", InputSchema: jsontext.Value(`{"type":"object"}`)}},
		Instructions: "Answer briefly.",
		ContextManagement: officina.ContextManagement{CompactAt: 50_000, ClearToolResults: officina.ToolResultClearing{
			After: 3, Keep: 1, AtLeastTokens: 5_000,
		}},
		Messages: []officina.Message{
			{Role: officina.User, Blocks: []officina.Block{{Text: "Hi"}}},
			{Role: officina.Operator, Blocks: []officina.Block{{Text: "Today is Monday."}}},
			{Role: officina.Assistant, Blocks: []officina.Block{{
				Raw:      jsontext.Value(`{"type":"tool_use","id":"t1","name":"search","input":{}}`),
				ToolCall: &officina.ToolCall{ID: "t1", Name: "search", Input: jsontext.Value(`{}`)},
			}}},
			{Role: officina.User, Blocks: []officina.Block{{ToolResult: &officina.ToolResult{CallID: "t1", Content: "ok"}}}},
		},
	}
	for range 2 {
		if _, err := collect(context.Background(), m, req); err != nil && !strings.Contains(err.Error(), "warm") {
			return err
		}
	}
	return nil
}

// response is one scripted answer of the fake API.
type response func(w http.ResponseWriter)

// fakeAPI is the Claude API's side of the network: it answers each request with the next scripted response and
// keeps the bodies it received, and their beta headers.
type fakeAPI struct {
	url string

	mu        sync.Mutex
	responses []response
	requests  []string
	// keys are the API keys the requests carried, and betas their beta headers.
	keys  []string
	betas []string
}

// serve starts a fake API that answers with responses, in order, and stops it when the test ends.
func serve(t *testing.T, responses ...response) *fakeAPI {
	t.Helper()
	api := &fakeAPI{responses: responses}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(r.Body)
		if err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		api.mu.Lock()
		api.requests = append(api.requests, string(body))
		api.keys = append(api.keys, r.Header.Get("X-Api-Key"))
		api.betas = append(api.betas, r.Header.Get("anthropic-beta"))
		var next response
		if len(api.responses) > 0 {
			next, api.responses = api.responses[0], api.responses[1:]
		}
		api.mu.Unlock()
		if next == nil {
			http.Error(w, `{"type":"error","error":{"type":"invalid_request_error","message":"no response left"}}`,
				http.StatusTeapot)
			return
		}
		next(w)
	}))
	t.Cleanup(srv.Close)
	api.url = srv.URL
	return api
}

// Requests returns the bodies received so far.
func (a *fakeAPI) Requests() []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]string(nil), a.requests...)
}

// Betas returns the anthropic-beta header of each request received so far.
func (a *fakeAPI) Betas() []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	return append([]string(nil), a.betas...)
}

// model returns a model on api with medium effort and opts.
func model(t *testing.T, api *fakeAPI, opts claude.Options) *claude.Model {
	t.Helper()
	m, err := claude.New(claude.Opus55, claude.EffortMedium, api.options(opts))
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	return m
}

// options returns opts with the API's address and a client that sends each request on a connection of its own, so
// no connection outlives the test.
func (a *fakeAPI) options(opts claude.Options) claude.Options {
	opts.APIKey, opts.BaseURL = "test-key", a.url
	opts.HTTPClient = &http.Client{Transport: &http.Transport{DisableKeepAlives: true}}
	return opts
}

// sse answers 200 with a stream of server-sent events.
func sse(body string) response {
	return func(w http.ResponseWriter) {
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = io.WriteString(w, body) // The client hanging up is the test's to see.
	}
}

// dropped answers 200 with the start of a stream, then drops the connection.
func dropped(body string) response {
	return func(w http.ResponseWriter) {
		w.Header().Set("Content-Type", "text/event-stream")
		_, _ = io.WriteString(w, body) // The connection is dropped next anyway.
		w.(http.Flusher).Flush()
		panic(http.ErrAbortHandler)
	}
}

// apiError answers with an error, as the API sends it, asking for retryAfter when it is not empty.
func apiError(status int, kind, message, retryAfter string) response {
	return func(w http.ResponseWriter) {
		w.Header().Set("Content-Type", "application/json")
		if retryAfter != "" {
			w.Header().Set("Retry-After", retryAfter)
		}
		w.WriteHeader(status)
		_, _ = fmt.Fprintf(w, `{"type":"error","error":{"type":%q,"message":%q}}`, kind, message)
	}
}

// unused is the handler of a tool a test never calls.
func unused(context.Context, jsontext.Value) (string, error) {
	return "", errors.New("unused tool")
}

// shared returns the path of a file of the repository's testdata: under OFFICINA_TESTDATA when it is set, as for
// mutation testing, which runs the tests in a copy of go/ alone; else beside the module.
func shared(path ...string) string {
	dir := os.Getenv("OFFICINA_TESTDATA")
	if dir == "" {
		dir = filepath.Join("..", "..", "..", "testdata")
	}
	return filepath.Join(append([]string{dir}, path...)...)
}

// fixture returns a file of the shared testdata, which the .NET implementation reads too.
func fixture(t *testing.T, name string) string {
	t.Helper()
	data, err := os.ReadFile(shared(name))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	return strings.ReplaceAll(string(data), "\r\n", "\n")
}

const start = `{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant",` +
	`"model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,` +
	`"usage":{"input_tokens":10,"output_tokens":1}}}`

// events returns server-sent events of the given data, each named by its type.
func events(data ...string) string {
	var b strings.Builder
	for _, d := range data {
		var event struct {
			Type string `json:"type"`
		}
		if err := json.Unmarshal([]byte(d), &event); err != nil {
			panic(fmt.Sprintf("events: %v in %s", err, d))
		}
		fmt.Fprintf(&b, "event: %s\ndata: %s\n\n", event.Type, d)
	}
	return b.String()
}

// textEvents return the events of a text block holding pieces, streamed in that order, at index 0.
func textEvents(pieces ...string) []string {
	data := []string{`{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}`}
	for _, p := range pieces {
		text, err := json.Marshal(p)
		if err != nil {
			panic(err)
		}
		data = append(data, `{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":`+string(text)+`}}`)
	}
	return append(data, `{"type":"content_block_stop","index":0}`)
}

// textReply returns a whole reply of one text block streamed in pieces ("Hello." if none), that stops for reason.
func textReply(reason string, pieces ...string) string {
	if len(pieces) == 0 {
		pieces = []string{"Hello."}
	}
	data := append([]string{start}, textEvents(pieces...)...)
	return events(append(data,
		`{"type":"message_delta","delta":{"stop_reason":"`+reason+`","stop_sequence":null},"usage":{"output_tokens":5}}`,
		`{"type":"message_stop"}`)...)
}

// esc returns s with each %u turned into the start of a JSON escape, so tests can spell escapes out.
func esc(s string) string {
	return strings.ReplaceAll(s, "%u", "\\u")
}

// hi returns a request of one user message.
func hi() officina.Request {
	return officina.Request{
		Instructions: "Answer briefly.",
		Messages:     []officina.Message{{Role: officina.User, Blocks: []officina.Block{{Text: "Hi"}}}},
	}
}

// outcome compares results by how the run ended, without their cost, counts and duration.
func outcome() cmp.Option {
	return cmpopts.IgnoreFields(officina.Result{}, "Cost", "ModelCalls", "ToolCalls", "Duration")
}

// collect streams req from m and returns its events and the error that ended them, if any.
func collect(ctx context.Context, m *claude.Model, req officina.Request) ([]officina.ModelEvent, error) {
	var got []officina.ModelEvent
	for event, err := range m.Stream(ctx, req) {
		if err != nil {
			return got, err
		}
		got = append(got, event)
	}
	return got, nil
}

// agent returns an agent of m with short instructions.
func agent(t *testing.T, m officina.Model) *officina.Agent {
	t.Helper()
	a, err := officina.NewAgent(m, "Answer briefly.", officina.AgentOptions{})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return a
}
