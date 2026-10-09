package claude_test

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"testing"
	"testing/synctest"
	"time"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

// The fake API runs outside the synctest bubble, so waiting on its network never holds the bubble's clock: in it,
// time.Since measures exactly the waits between attempts.

// cutOff is a reply that streams "Hel" and reports 3 output tokens, then fails with an overload mid-stream.
func cutOff() string {
	return events(append(append([]string{start}, textEvents("Hel")[:2]...),
		`{"type":"message_delta","delta":{"stop_reason":null,"stop_sequence":null},"usage":{"output_tokens":3}}`,
		`{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}`)...)
}

func TestModel_MDL04_ATransientFailureWaitsThenSucceeds(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name     string
		failures []response
		// minWait and maxWait bound the total wait.
		minWait, maxWait time.Duration
	}{
		{"a rate limit waits as long as Retry-After asks",
			[]response{apiError(429, "rate_limit_error", "Slow down.", "7")}, 7 * time.Second, 7 * time.Second},
		{"a Retry-After date is honoured too",
			[]response{apiError(429, "rate_limit_error", "Slow down.", "Thu, 01 Jan 2099 00:00:00 GMT")},
			30 * time.Second, 30 * time.Second},
		{"a long Retry-After is capped",
			[]response{apiError(429, "rate_limit_error", "Slow down.", "3600")}, 30 * time.Second, 30 * time.Second},
		{"a Retry-After too long for a duration is capped too",
			[]response{apiError(429, "rate_limit_error", "Slow down.", "10000000000")}, 30 * time.Second, 30 * time.Second},
		{"an overload then a server error back off exponentially with jitter",
			[]response{apiError(529, "overloaded_error", "Overloaded", ""), apiError(500, "api_error", "Oops", "")},
			1500 * time.Millisecond, 3 * time.Second},
		{"any server error is retried, by its status alone", []response{apiError(500, "unknown_error", "Oops", "")},
			500 * time.Millisecond, time.Second},
		{"a request timeout is retried", []response{apiError(408, "timeout_error", "Timeout", "")},
			500 * time.Millisecond, time.Second},
		{"a conflict is retried", []response{apiError(409, "conflict_error", "Conflict", "")},
			500 * time.Millisecond, time.Second},
		{"an error event mid-stream is retried", []response{sse(cutOff())}, 500 * time.Millisecond, time.Second},
		{"a connection dropped mid-stream is retried", []response{dropped(events(start, textEvents("Hel")[0]))},
			500 * time.Millisecond, time.Second},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			api := serve(t, append(tt.failures, sse(textReply("end_turn")))...)
			synctest.Test(t, func(t *testing.T) {
				m := model(t, api, claude.Options{})
				began := time.Now()

				got, err := collect(t.Context(), m, hi())
				if err != nil {
					t.Fatalf("Stream() error = %v", err)
				}

				if waited := time.Since(began); waited < tt.minWait || waited > tt.maxWait {
					t.Errorf("waited %v, want %v to %v", waited, tt.minWait, tt.maxWait)
				}
				if last := got[len(got)-1]; last != (officina.Finished{Reason: officina.FinishEnd}) {
					t.Errorf("last event = %#v, want the end", last)
				}
			})
			requests := api.Requests()
			if len(requests) != len(tt.failures)+1 || requests[0] != requests[len(requests)-1] {
				t.Errorf("got %d requests, want %d of the same body", len(requests), len(tt.failures)+1)
			}
		})
	}
}

func TestModel_MDL04_AFailureMidStreamRestartsTheReplyKeepingTheTokensItUsed(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(cutOff()), sse(textReply("end_turn", "Hello.")))
	synctest.Test(t, func(t *testing.T) {
		got, err := collect(t.Context(), model(t, api, claude.Options{}), hi())
		if err != nil {
			t.Fatalf("Stream() error = %v", err)
		}

		want := []officina.ModelEvent{
			officina.TextDelta{Text: "Hel"},
			officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 3}},
			officina.Retried{},
			officina.TextDelta{Text: "Hello."},
			officina.BlockReceived{Block: officina.Block{Text: "Hello.", Raw: []byte(`{"type":"text","text":"Hello."}`)}},
			officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 5}},
			officina.Finished{Reason: officina.FinishEnd},
		}
		if diff := cmp.Diff(want, got); diff != "" {
			t.Errorf("events mismatch (-want +got):\n%s", diff)
		}
	})
}

func TestModel_MDL04_ARestartedReplyReachesTheRunOnceAndTheHostIsTold(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(cutOff()), sse(textReply("end_turn", "Hello.")))
	synctest.Test(t, func(t *testing.T) {
		var c officina.Conversation
		events, result := agent(t, model(t, api, claude.Options{})).Stream(t.Context(), &c, "Hi", officina.RunOptions{})
		var got []officina.RunEvent
		for event := range events {
			if _, ok := event.(officina.ConversationAppended); !ok {
				got = append(got, event)
			}
		}
		res, err := result()
		if err != nil {
			t.Fatalf("Stream() error = %v", err)
		}

		want := []officina.RunEvent{
			officina.TextStreamed{Text: "Hel"},
			officina.UsageReported{Usage: officina.Usage{Input: 10, Output: 3}, Cost: 0.0001},
			officina.ReplyRestarted{},
			officina.TextStreamed{Text: "Hello."},
			officina.UsageReported{Usage: officina.Usage{Input: 10, Output: 5}, Cost: 0.00014},
		}
		if diff := cmp.Diff(want, got); diff != "" {
			t.Errorf("events mismatch (-want +got):\n%s", diff)
		}
		wantResult := officina.Result{Status: officina.Completed, Text: "Hello.", Usage: officina.Usage{Input: 20, Output: 8}}
		if diff := cmp.Diff(wantResult, res, outcome()); diff != "" {
			t.Errorf("result mismatch (-want +got):\n%s", diff)
		}
		if n := len(c.Messages()[1].Blocks); n != 1 {
			t.Errorf("the reply has %d blocks, want only the last attempt's one", n)
		}
	})
}

func TestModel_MDL04_AFailureThatPersistsIsTransientAfterEveryAttempt(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name    string
		failure func() response
		// usage is what each attempt reports before it fails.
		usage []officina.ModelEvent
	}{
		{"before the stream", func() response { return apiError(529, "overloaded_error", "Overloaded", "") }, nil},
		{"mid-stream", func() response {
			return sse(events(start, textEvents("x")[0], `{"type":"error",`+
				`"error":{"type":"overloaded_error","message":"Overloaded"}}`))
		},
			[]officina.ModelEvent{officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 1}}}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			failures := make([]response, 5)
			for i := range failures {
				failures[i] = tt.failure()
			}
			api := serve(t, failures...)
			synctest.Test(t, func(t *testing.T) {
				got, err := collect(t.Context(), model(t, api, claude.Options{}), hi())

				if !errors.Is(err, claude.ErrTransient) {
					t.Errorf("Stream() error = %v, want ErrTransient", err)
				}
				var want []officina.ModelEvent
				for i := range 5 {
					want = append(want, tt.usage...)
					if i < 4 {
						want = append(want, officina.Retried{})
					}
				}
				if diff := cmp.Diff(want, got); diff != "" {
					t.Errorf("events mismatch (-want +got):\n%s", diff)
				}
			})
			if n := len(api.Requests()); n != 5 {
				t.Errorf("got %d requests, want 5", n)
			}
		})
	}
}

func TestModel_MDL04_CancellingDuringARetryWaitEndsTheCallAtOnce(t *testing.T) {
	t.Parallel()
	api := serve(t, apiError(429, "rate_limit_error", "Slow down.", "20"))
	synctest.Test(t, func(t *testing.T) {
		ctx, cancel := context.WithCancel(t.Context())
		t.Cleanup(cancel)
		began := time.Now()
		var err error
		for event, e := range model(t, api, claude.Options{}).Stream(ctx, hi()) {
			if event == (officina.Retried{}) {
				time.AfterFunc(time.Second, cancel)
			}
			err = e
		}

		if !errors.Is(err, context.Canceled) {
			t.Errorf("Stream() error = %v, want context.Canceled", err)
		}
		if waited := time.Since(began); waited != time.Second {
			t.Errorf("waited %v, want 1s", waited)
		}
	})
	if n := len(api.Requests()); n != 1 {
		t.Errorf("got %d requests, want 1", n)
	}
}

func TestModel_MDL04_ErrorsRetryingCannotFixAreClassifiedAndNotRetried(t *testing.T) {
	t.Parallel()
	tests := []struct {
		status int
		kind   string
		want   error
	}{
		{http.StatusBadRequest, "invalid_request_error", claude.ErrInvalidRequest},
		{http.StatusUnauthorized, "authentication_error", claude.ErrAuthentication},
		{http.StatusForbidden, "permission_error", claude.ErrAuthentication},
		{http.StatusNotFound, "not_found_error", claude.ErrInvalidRequest},
		{http.StatusRequestEntityTooLarge, "request_too_large", claude.ErrInvalidRequest},
	}
	for _, tt := range tests {
		t.Run(tt.kind, func(t *testing.T) {
			t.Parallel()
			api := serve(t, apiError(tt.status, tt.kind, "No.", ""))

			_, err := collect(t.Context(), model(t, api, claude.Options{}), hi())

			if !errors.Is(err, tt.want) {
				t.Errorf("Stream() error = %v, want %v", err, tt.want)
			}
			if n := len(api.Requests()); n != 1 {
				t.Errorf("got %d requests, want 1", n)
			}
		})
	}
}

func TestModel_MDL04_APromptLongerThanTheContextWindowFinishesAsContextFull(t *testing.T) {
	t.Parallel()
	api := serve(t, apiError(400, "invalid_request_error", "prompt is too long: 1000001 tokens > 1000000 maximum", ""))

	result, err := agent(t, model(t, api, claude.Options{})).Run(t.Context(), nil, "Hi", officina.RunOptions{})
	if err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.ContextFull}, result, outcome()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_MDL04_AFailedRunCarriesTheFailuresClass(t *testing.T) {
	t.Parallel()
	api := serve(t, apiError(401, "authentication_error", "invalid x-api-key", ""))

	result, err := agent(t, model(t, api, claude.Options{})).Run(t.Context(), nil, "Hi", officina.RunOptions{})
	if err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	if result.Status != officina.Failed || result.Failure != officina.ModelError ||
		!strings.Contains(result.Detail, claude.ErrAuthentication.Error()) {
		t.Errorf("result = %+v, want a model error saying %q", result, claude.ErrAuthentication)
	}
}
