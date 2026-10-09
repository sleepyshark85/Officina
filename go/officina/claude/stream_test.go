package claude_test

import (
	"encoding/json/jsontext"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

func TestModel_MDL01_MDL05_TextStreamsAndEachBlockComesCompleteInTheCanonicalForm(t *testing.T) {
	t.Parallel()
	m := model(t, serve(t, sse(fixture(t, "claude/thinking-text-tool.sse"))), claude.Options{})

	got, err := collect(t.Context(), m, hi())
	if err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	want := []officina.ModelEvent{
		officina.BlockReceived{Block: officina.Block{
			Raw: jsontext.Value(`{"type":"thinking","thinking":"","signature":"EqQBCkYIBRgCKkB+sig/a=="}`),
		}},
		officina.TextDelta{Text: "Looking up "},
		officina.TextDelta{Text: "«Café Libro»."},
		officina.BlockReceived{Block: officina.Block{
			Text: "Looking up «Café Libro».", Raw: jsontext.Value(`{"type":"text","text":"Looking up «Café Libro»."}`),
		}},
		// The raw input compacted, as its spacing would not survive the SDK's encoder; the call's input as written.
		officina.BlockReceived{Block: officina.Block{
			ToolCall: &officina.ToolCall{ID: "toolu_01", Name: "search", Input: jsontext.Value(`{"query": "Gaudy Night"}`)},
			Raw:      jsontext.Value(`{"type":"tool_use","id":"toolu_01","name":"search","input":{"query":"Gaudy Night"}}`),
		}},
		officina.UsageReceived{Usage: officina.Usage{Input: 12, Output: 42, CacheRead: 2048, CacheWrite: 300}},
		officina.Finished{Reason: officina.FinishToolUse},
	}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("events mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_MDL05_BlocksAreStoredWithHTMLCharactersEscapedAndOtherEscapesAsReceived(t *testing.T) {
	t.Parallel()
	reply := events(start,
		`{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}`,
		esc(`{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"<b>Caf%u00e9 & a\/b</b>"}}`),
		`{"type":"content_block_stop","index":0}`,
		`{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"t1","name":"s","input":{}}}`,
		esc(`{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"q\": \"Caf\%u00e9 <x>\"}"}}`),
		`{"type":"content_block_stop","index":1}`,
		`{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}`,
		`{"type":"message_stop"}`)
	m := model(t, serve(t, sse(reply)), claude.Options{})

	got, err := collect(t.Context(), m, hi())
	if err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	var raws []string
	for _, e := range got {
		if b, ok := e.(officina.BlockReceived); ok {
			raws = append(raws, string(b.Block.Raw))
		}
	}
	want := []string{
		esc(`{"type":"text","text":"%u003cb%u003eCafé %u0026 a/b%u003c/b%u003e"}`),
		esc(`{"type":"tool_use","id":"t1","name":"s","input":{"q":"Caf%u00e9 %u003cx%u003e"}}`),
	}
	if diff := cmp.Diff(want, raws); diff != "" {
		t.Errorf("stored blocks mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_MDL06_ARefusalFinishesWithItsCategoryAndTheRunStops(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(fixture(t, "claude/refusal.sse")))

	result, err := agent(t, model(t, api, claude.Options{})).Run(t.Context(), nil, "Hi", officina.RunOptions{})
	if err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	want := officina.Result{Status: officina.Stopped, Stop: officina.Refusal, Detail: "cyber",
		Usage: officina.Usage{Input: 9, Output: 7}}
	if diff := cmp.Diff(want, result, outcome()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_MDL01_StopReasonsMapByTheirWordAndAnUnknownOneKeepsIt(t *testing.T) {
	t.Parallel()
	tests := []struct {
		reason string
		want   officina.Finished
	}{
		{"end_turn", officina.Finished{Reason: officina.FinishEnd}},
		{"tool_use", officina.Finished{Reason: officina.FinishToolUse}},
		{"max_tokens", officina.Finished{Reason: officina.FinishMaxTokens}},
		{"model_context_window_exceeded", officina.Finished{Reason: officina.FinishContextFull}},
		{"pause_turn", officina.Finished{Reason: officina.FinishUnknown, Detail: "pause_turn"}},
		{"a_reason_from_the_future", officina.Finished{Reason: officina.FinishUnknown, Detail: "a_reason_from_the_future"}},
	}
	for _, tt := range tests {
		t.Run(tt.reason, func(t *testing.T) {
			t.Parallel()
			m := model(t, serve(t, sse(textReply(tt.reason))), claude.Options{})

			got, err := collect(t.Context(), m, hi())
			if err != nil {
				t.Fatalf("Stream() error = %v", err)
			}

			if diff := cmp.Diff(officina.ModelEvent(tt.want), got[len(got)-1]); diff != "" {
				t.Errorf("last event mismatch (-want +got):\n%s", diff)
			}
		})
	}
}

func TestModel_CTX05_UsageCountsCacheReadsAndWritesAndAddsUpEveryIteration(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name  string
		reply string
		want  officina.Usage
	}{
		{"one call", fixture(t, "claude/thinking-text.sse"),
			officina.Usage{Input: 12, Output: 42, CacheRead: 2048, CacheWrite: 300}},
		{"a compaction then the reply", fixture(t, "claude/compaction-iterations.sse"),
			officina.Usage{Input: 50, Output: 663, CacheRead: 5230, CacheWrite: 50648}},
		{"an iteration of an unknown kind", events(start, `{"type":"message_delta","delta":{"stop_reason":"end_turn",`+
			`"stop_sequence":null},"usage":{"input_tokens":2,"output_tokens":5,"iterations":[{"type":"from_the_future",`+
			`"input_tokens":30,"cache_read_input_tokens":10,"cache_creation_input_tokens":0,"output_tokens":20},`+
			`{"type":"message","input_tokens":2,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,`+
			`"output_tokens":5}]}}`, `{"type":"message_stop"}`),
			officina.Usage{Input: 32, Output: 25, CacheRead: 10}},
		// The split of cache writes by lifetime comes with the message's start only.
		{"cache writes for an hour", events(strings.Replace(start, `"usage":{`, `"usage":{"cache_creation":`+
			`{"ephemeral_5m_input_tokens":100,"ephemeral_1h_input_tokens":200},`, 1),
			`{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":2,`+
				`"output_tokens":5,"cache_creation_input_tokens":300}}`, `{"type":"message_stop"}`),
			officina.Usage{Input: 2, Output: 5, CacheWrite: 300, CacheWriteHour: 200}},
		{"cache writes for an hour in iterations", events(start, `{"type":"message_delta","delta":{"stop_reason":`+
			`"end_turn","stop_sequence":null},"usage":{"input_tokens":2,"output_tokens":5,"iterations":[{"type":`+
			`"compaction","input_tokens":30,"cache_read_input_tokens":0,"cache_creation_input_tokens":40,`+
			`"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":40},"output_tokens":20},`+
			`{"type":"message","input_tokens":2,"cache_read_input_tokens":0,"cache_creation_input_tokens":7,`+
			`"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":7},"output_tokens":5}]}}`,
			`{"type":"message_stop"}`),
			officina.Usage{Input: 32, Output: 25, CacheWrite: 47, CacheWriteHour: 47}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			m := model(t, serve(t, sse(tt.reply)), claude.Options{})

			got, err := collect(t.Context(), m, hi())
			if err != nil {
				t.Fatalf("Stream() error = %v", err)
			}

			var usage []officina.Usage
			for _, e := range got {
				if u, ok := e.(officina.UsageReceived); ok {
					usage = append(usage, u.Usage)
				}
			}
			if diff := cmp.Diff([]officina.Usage{tt.want}, usage); diff != "" {
				t.Errorf("usage mismatch (-want +got):\n%s", diff)
			}
		})
	}
}

func TestModel_EVT01_AConsumerThatStopsEarlyEndsTheCall(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(textReply("end_turn", "Hel", "lo.")))
	m := model(t, api, claude.Options{})

	var got []officina.ModelEvent
	for event, err := range m.Stream(t.Context(), hi()) {
		if err != nil {
			t.Fatalf("Stream() error = %v", err)
		}
		got = append(got, event)
		break
	}

	// No goroutine is left behind either: TestMain checks it.
	if diff := cmp.Diff([]officina.ModelEvent{officina.TextDelta{Text: "Hel"}}, got); diff != "" {
		t.Errorf("events mismatch (-want +got):\n%s", diff)
	}
}
