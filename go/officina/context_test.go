package officina_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// compactingModel is a scripted model whose provider compacts conversations and clears old tool results.
type compactingModel struct {
	*officinatest.Model
}

func compacting(m *officinatest.Model) compactingModel {
	return compactingModel{m}
}

func (compactingModel) Info() officina.ModelInfo {
	return officina.ModelInfo{Provider: "scripted", Name: "scripted", Compacts: true, ClearsToolResults: true}
}

// managed returns context management that compacts and clears, as a demo does.
func managed() officina.ContextManagement {
	return officina.ContextManagement{CompactAt: 50_000, ClearToolResults: officina.ToolResultClearing{After: 12, Keep: 10}}
}

// compactingReply returns a reply in which the provider cleared old tool results, then compacted the conversation
// into a summary block, with characters a re-encoder would change.
func compactingReply() officinatest.Reply {
	return officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officina.Block{Raw: jsontext.Value(
			`{"type":"compaction","content":"Summary: shelf Q2, \` + `u00abCaf\` + `u00e9\` + `u00bb \` + `u003cnew\` + `u003e"}`)}},
		officina.ClearingReported{Tokens: 4_892, ToolCalls: 2},
		officina.CompactionReported{Tokens: 52_753, SummaryTokens: 578},
		officina.TextDelta{Text: "Shelf Q2."},
		officina.BlockReceived{Block: officinatest.TextBlock("Shelf Q2.")},
		officina.UsageReceived{Usage: officina.Usage{Input: 50, Output: 663, CacheRead: 5230, CacheWrite: 50648}},
		officina.Finished{Reason: officina.FinishEnd},
	}}
}

func TestRun_HIST01_HIST04_ACompactionAndAClearingAreReportedAuditedAndTheBlockReplayedAsReceived(t *testing.T) {
	t.Parallel()
	model := compacting(officinatest.NewModel("scripted", compactingReply(), officinatest.TextReply("You're welcome.")))
	sink := &memorySink{}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{AuditSink: sink, ContextManagement: managed()})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	c := officina.Conversation{ID: "s1"}

	events, _ := stream(t.Context(), t, agent, &c, "Which shelf?", officina.RunOptions{}, func(officina.RunEvent) bool {
		return true
	})
	saved, err := json.Marshal(&c)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	var resumed officina.Conversation
	if err := json.Unmarshal(saved, &resumed); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	run(t, agent, &resumed, "Thanks.", officina.RunOptions{})

	var edits []officina.RunEvent
	for _, e := range events {
		switch e.(type) {
		case officina.ConversationCompacted, officina.ToolResultsCleared:
			edits = append(edits, e)
		}
	}
	want := []officina.RunEvent{
		officina.ToolResultsCleared{Tokens: 4_892, ToolCalls: 2},
		officina.ConversationCompacted{Tokens: 52_753, SummaryTokens: 578},
	}
	if diff := cmp.Diff(want, edits); diff != "" {
		t.Errorf("context edit events mismatch (-want +got):\n%s", diff)
	}
	requests := model.Requests()
	block := compactingReply().Events[0].(officina.BlockReceived).Block.Raw
	if got := requests[1].Messages[1].Blocks[0].Raw; string(got) != string(block) {
		t.Errorf("replayed compaction block = %s, want it as received: %s", got, block)
	}
	for i, r := range requests {
		if r.ContextManagement != managed() {
			t.Errorf("request %d context management = %+v, want %+v", i+1, r.ContextManagement, managed())
		}
	}
	var trail []officina.AuditEntry
	for _, e := range sink.Entries() {
		if e.Kind == officina.AuditCompacted || e.Kind == officina.AuditCleared {
			trail = append(trail, officina.AuditEntry{Kind: e.Kind, Detail: e.Detail})
		}
	}
	wantTrail := []officina.AuditEntry{
		{Kind: officina.AuditCleared, Detail: "Results of 2 tool calls cleared: 4,892 tokens."},
		{Kind: officina.AuditCompacted, Detail: "52,753 tokens summarized into 578."},
	}
	if diff := cmp.Diff(wantTrail, trail); diff != "" {
		t.Errorf("audit trail mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_HIST03_AProviderWithoutCompactionEndsWithContextFullAndAppendsNothing(t *testing.T) {
	t.Parallel()
	// The scripted model's provider compacts nothing, so the window filling ends the run.
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."),
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.UsageReceived{Usage: officina.Usage{Input: 1_000_000}},
			officina.Finished{Reason: officina.FinishContextFull},
		}})
	agent := newAgent(t, model)
	var c officina.Conversation
	run(t, agent, &c, "Hi", officina.RunOptions{})
	before := c.Messages()

	result := run(t, agent, &c, "And now?", officina.RunOptions{})

	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.ContextFull,
		Usage: officina.Usage{Input: 1_000_000}}, result, outcome()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff(before, c.Messages()); diff != "" {
		t.Errorf("conversation changed (-before +after):\n%s", diff)
	}
}

func TestNewAgent_HIST03_ContextManagementNeedsValidSettingsAndTheProvidersSupport(t *testing.T) {
	t.Parallel()
	scripted := officinatest.NewModel("scripted")
	tests := []struct {
		name  string
		model officina.Model
		cm    officina.ContextManagement
		want  string
	}{
		{"compaction its provider lacks", scripted, officina.ContextManagement{CompactAt: 50_000},
			"new agent: the model's provider does not compact conversations"},
		{"clearing its provider lacks", scripted,
			officina.ContextManagement{ClearToolResults: officina.ToolResultClearing{After: 3}},
			"new agent: the model's provider does not clear old tool results"},
		{"negative threshold", compacting(scripted), officina.ContextManagement{CompactAt: -1},
			"new agent: the compaction threshold -1 is negative"},
		{"clearing without a threshold", compacting(scripted),
			officina.ContextManagement{ClearToolResults: officina.ToolResultClearing{Keep: 2}},
			"new agent: tool results are cleared after 0 tool calls; it must be at least 1"},
		{"negative keep", compacting(scripted),
			officina.ContextManagement{ClearToolResults: officina.ToolResultClearing{After: 3, Keep: -1}},
			"new agent: tool results clearing keeps -1 tool calls; it cannot keep fewer than 0"},
		{"negative minimum", compacting(scripted),
			officina.ContextManagement{ClearToolResults: officina.ToolResultClearing{After: 3, AtLeastTokens: -1}},
			"new agent: tool results clearing clears at least -1 tokens; it cannot clear fewer than 0"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			agent, err := officina.NewAgent(tt.model, instructions, officina.AgentOptions{ContextManagement: tt.cm})

			if err == nil || err.Error() != tt.want || agent != nil {
				t.Errorf("NewAgent() = %v, %v; want nil, %q", agent, err, tt.want)
			}
		})
	}
}

func TestRun_CTX04_HIST01_ContextManagementIsPartOfThePrefix(t *testing.T) {
	t.Parallel()
	settings := []officina.ContextManagement{
		{},
		{CompactAt: 50_000},
		{CompactAt: 60_000},
		{ClearToolResults: officina.ToolResultClearing{After: 12}},
		{ClearToolResults: officina.ToolResultClearing{After: 1}},
		{ClearToolResults: officina.ToolResultClearing{After: 12, Keep: 1}},
		{ClearToolResults: officina.ToolResultClearing{After: 12, AtLeastTokens: 1}},
		managed(),
	}
	model := compacting(officinatest.NewModel("scripted", officinatest.TextReply("Hi.")))
	var c officina.Conversation
	first, err := officina.NewAgent(model, instructions, officina.AgentOptions{})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	run(t, first, &c, "Hi", officina.RunOptions{})

	for i, cm := range settings {
		agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{ContextManagement: cm})
		if err != nil {
			t.Fatalf("NewAgent(%+v) error = %v", cm, err)
		}
		if got := agent.CanContinue(&c); got != (i == 0) {
			t.Errorf("CanContinue() with %+v = %v, want %v", cm, got, i == 0)
		}
	}
}
