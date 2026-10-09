package claude_test

import (
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

// both returns context management that asks for clearing and compaction, as the .NET implementation's tests do.
func both() officina.ContextManagement {
	return officina.ContextManagement{
		CompactAt: 50_000, ClearToolResults: officina.ToolResultClearing{After: 3, Keep: 1, AtLeastTokens: 5_000},
	}
}

// sent returns the context_management of a request body as parsed JSON, and whether it has one.
func sent(t *testing.T, body string) (any, bool) {
	t.Helper()
	var request map[string]any
	if err := json.Unmarshal([]byte(body), &request); err != nil {
		t.Fatalf("unmarshal the request: %v", err)
	}
	cm, ok := request["context_management"]
	return cm, ok
}

func TestModel_HIST01_HIST02_ContextManagementAsksForClearingThenThresholdCompactionWithTheirBetas(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name    string
		context officina.ContextManagement
		// dotnet is what the .NET implementation sends, from its own test of the same settings.
		dotnet string
		betas  string
	}{
		{"both", both(),
			`{"edits":[{"type":"clear_tool_uses_20250919","trigger":{"type":"tool_uses","value":3},` +
				`"keep":{"type":"tool_uses","value":1},"clear_at_least":{"type":"input_tokens","value":5000}},` +
				`{"type":"compact_20260112","trigger":{"type":"input_tokens","value":50000}}]}`,
			"context-management-2025-06-27,compact-2026-01-12"},
		{"compaction only", officina.ContextManagement{CompactAt: 60_000},
			`{"edits":[{"type":"compact_20260112","trigger":{"type":"input_tokens","value":60000}}]}`,
			"compact-2026-01-12"},
		{"clearing that keeps none and clears whatever there is",
			officina.ContextManagement{ClearToolResults: officina.ToolResultClearing{After: 12}},
			`{"edits":[{"type":"clear_tool_uses_20250919","trigger":{"type":"tool_uses","value":12},` +
				`"keep":{"type":"tool_uses","value":0}}]}`,
			"context-management-2025-06-27"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			api := serve(t, sse(textReply("end_turn")))
			req := hi()
			req.ContextManagement = tt.context

			if _, err := collect(t.Context(), model(t, api, claude.Options{}), req); err != nil {
				t.Fatalf("Stream() error = %v", err)
			}

			got, _ := sent(t, api.Requests()[0])
			var want any
			if err := json.Unmarshal([]byte(tt.dotnet), &want); err != nil {
				t.Fatalf("unmarshal .NET's context management: %v", err)
			}
			if diff := cmp.Diff(want, got); diff != "" {
				t.Errorf("context_management mismatch (-want +got):\n%s", diff)
			}
			if got := api.Betas()[0]; got != tt.betas {
				t.Errorf("anthropic-beta = %q, want %q", got, tt.betas)
			}
		})
	}
}

func TestModel_HIST01_WithoutContextManagementTheRequestHasNoneAndNoBetas(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(textReply("end_turn")))

	if _, err := collect(t.Context(), model(t, api, claude.Options{}), hi()); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	if cm, ok := sent(t, api.Requests()[0]); ok {
		t.Errorf("context_management = %v, want none", cm)
	}
	if got := api.Betas()[0]; got != "" {
		t.Errorf("anthropic-beta = %q, want none", got)
	}
}

func TestModel_HIST04_ACompactionIsReportedFromItsIterationAndPricedWithTheReply(t *testing.T) {
	t.Parallel()
	m := model(t, serve(t, sse(fixture(t, "claude/compaction-iterations.sse"))), claude.Options{})

	got, err := collect(t.Context(), m, hi())
	if err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	// The compaction iteration read 48 + 2,615 cached + 50,090 written tokens, and wrote a 578-token summary.
	usage := officina.Usage{Input: 50, Output: 663, CacheRead: 5230, CacheWrite: 50648}
	want := []officina.ModelEvent{
		officina.CompactionReported{Tokens: 52_753, SummaryTokens: 578},
		officina.UsageReceived{Usage: usage},
		officina.Finished{Reason: officina.FinishEnd},
	}
	if diff := cmp.Diff(want, got[len(got)-3:]); diff != "" {
		t.Errorf("events mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_HIST04_AClearingIsReportedFromTheAppliedEdits(t *testing.T) {
	t.Parallel()
	m := model(t, serve(t, sse(fixture(t, "claude/clearing.sse"))), claude.Options{})

	got, err := collect(t.Context(), m, hi())
	if err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	var edits []officina.ModelEvent
	for _, e := range got {
		switch e.(type) {
		case officina.CompactionReported, officina.ClearingReported:
			edits = append(edits, e)
		}
	}
	if diff := cmp.Diff([]officina.ModelEvent{officina.ClearingReported{Tokens: 4_892, ToolCalls: 2}}, edits); diff != "" {
		t.Errorf("context edits mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff(officina.ModelEvent(officina.Finished{Reason: officina.FinishToolUse}), got[len(got)-1]); diff != "" {
		t.Errorf("last event mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_HIST01_MDL05_TheCompactionBlockIsKeptAndReplayedAsReceivedAndTheRunReportsIt(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(fixture(t, "claude/compaction-iterations.sse")), sse(textReply("end_turn")))
	a, err := officina.NewAgent(model(t, api, claude.Options{}), "Answer briefly.",
		officina.AgentOptions{ContextManagement: both()})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	var c officina.Conversation

	var compactions []officina.ConversationCompacted
	events, result := a.Stream(t.Context(), &c, "Which shelf?", officina.RunOptions{})
	for e := range events {
		if compacted, ok := e.(officina.ConversationCompacted); ok {
			compactions = append(compactions, compacted)
		}
	}
	if _, err := result(); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}
	saved, err := json.Marshal(&c)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	var resumed officina.Conversation
	if err := json.Unmarshal(saved, &resumed); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	if _, err := a.Run(t.Context(), &resumed, "Thanks.", officina.RunOptions{}); err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	if diff := cmp.Diff([]officina.ConversationCompacted{{Tokens: 52_753, SummaryTokens: 578}}, compactions); diff != "" {
		t.Errorf("compactions mismatch (-want +got):\n%s", diff)
	}
	reply := c.Messages()[1].Blocks
	const compaction = `{"type":"compaction","content":"Summary: the customer asked about shelf Q2."}`
	if got := string(reply[0].Raw); got != compaction {
		t.Errorf("compaction block = %s, want %s", got, compaction)
	}
	raws := make([]string, len(reply))
	for i, b := range reply {
		raws[i] = string(b.Raw)
	}
	requests := api.Requests()
	if want := `{"role":"assistant","content":[` + strings.Join(raws, ",") + `]}`; !strings.Contains(requests[1], want) {
		t.Errorf("request %s does not hold the reply %s byte for byte", requests[1], want)
	}
	for i, r := range requests {
		if !strings.Contains(r, `"compact_20260112"`) {
			t.Errorf("request %d has no compaction: %s", i+1, r)
		}
	}
}
