package officina_test

import (
	"bufio"
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/google/go-cmp/cmp"
	"github.com/google/go-cmp/cmp/cmpopts"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// auditAgent returns an agent of model and tools with sink, an approver that approves once, and a name.
func auditAgent(t *testing.T, model officina.Model, sink officina.AuditSink, secrets []string,
	tools ...officina.Tool,
) *officina.Agent {
	t.Helper()
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools: tools, AuditSink: sink, Name: "bookshop", Secrets: secrets,
		Approver: officinatest.NewApprover(officina.Approval{Approved: true}),
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return agent
}

// htmlEscaped returns s as the inside of a JSON string with <, > and & escaped, as a provider may write it.
func htmlEscaped(s string) string {
	quoted, err := json.Marshal(s, jsontext.EscapeForHTML(true))
	if err != nil {
		panic(err)
	}
	return string(quoted[1 : len(quoted)-1])
}

// ignoreVarying leaves out the fields of an entry that vary from run to run.
func ignoreVarying() cmp.Option {
	return cmpopts.IgnoreFields(officina.AuditEntry{}, "Time", "Run", "Duration")
}

func TestRun_AUD01_AUD03_TheTrailRecordsTheRunAndEachStepInOrder(t *testing.T) {
	t.Parallel()
	order := handlerTool("order", officina.Write, func(context.Context, jsontext.Value) (string, error) {
		return "ordered", nil
	})
	order.NeedsApproval = true
	model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officinatest.ToolUseBlock("c1", "order", `{"isbn":"1"}`)},
		officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 2}},
		officina.Finished{Reason: officina.FinishToolUse},
	}}, officinatest.TextReply("Ordered."))
	sink := &memorySink{}
	c := officina.Conversation{ID: "session-1"}
	before := time.Now()

	run(t, auditAgent(t, model, sink, nil, order), &c, "Order it", officina.RunOptions{})

	entry := func(seq int64, kind officina.AuditKind, e officina.AuditEntry) officina.AuditEntry {
		e.Sequence, e.Kind, e.Conversation, e.Agent = seq, kind, "session-1", "bookshop"
		return e
	}
	want := []officina.AuditEntry{
		entry(1, officina.AuditRunStarted, officina.AuditEntry{}),
		entry(2, officina.AuditApprovalAsked, officina.AuditEntry{Tool: "order", CallID: "c1", Input: `{"isbn":"1"}`}),
		entry(3, officina.AuditApprovalAnswered, officina.AuditEntry{Tool: "order", CallID: "c1", Outcome: "approved"}),
		entry(4, officina.AuditToolStarted, officina.AuditEntry{Tool: "order", CallID: "c1", Input: `{"isbn":"1"}`}),
		entry(5, officina.AuditToolEnded, officina.AuditEntry{
			Tool: "order", CallID: "c1", Input: `{"isbn":"1"}`, Outcome: "ok", Detail: "ordered",
		}),
		entry(6, officina.AuditRunEnded, officina.AuditEntry{
			Outcome: "Completed", Usage: officina.Usage{Input: 10, Output: 2},
		}),
	}
	got := sink.Entries()
	if diff := cmp.Diff(want, got, ignoreVarying()); diff != "" {
		t.Errorf("entries mismatch (-want +got):\n%s", diff)
	}
	for _, e := range got {
		if e.Run != got[0].Run || e.Run == "" || e.Time.Before(before) {
			t.Errorf("entry %d has run %q at %v; want the run's id, after the run started", e.Sequence, e.Run, e.Time)
		}
	}
}

func TestRun_AUD01_TheEndEntrySaysHowTheRunEnded(t *testing.T) {
	t.Parallel()
	sink := &memorySink{}
	model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officinatest.TextBlock("No.")},
		officina.Finished{Reason: officina.FinishRefusal, Detail: "cyber"},
	}})

	run(t, auditAgent(t, model, sink, nil), nil, "Hi", officina.RunOptions{})

	got := sink.Entries()
	if last := got[len(got)-1]; last.Kind != officina.AuditRunEnded || last.Outcome != "Stopped: Refusal" ||
		last.Detail != "cyber" {
		t.Errorf("last entry = %+v, want the run ended, stopped by a refusal", last)
	}
}

func TestRun_AUD02_AWriteWhoseAttemptCannotBeRecordedNeverRuns(t *testing.T) {
	t.Parallel()
	var ran []string
	tracked := func(name string, kind officina.ToolKind) officina.Tool {
		return handlerTool(name, kind, func(context.Context, jsontext.Value) (string, error) {
			ran = append(ran, name)
			return "done", nil
		})
	}
	sink := &memorySink{fail: func(e officina.AuditEntry) bool { return e.Kind == officina.AuditToolStarted }}
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
		officinatest.ToolUseBlock("c1", "order", `{}`), officinatest.ToolUseBlock("c2", "search", `{}`),
	), officinatest.TextReply("The order failed."))
	var c officina.Conversation

	result := run(t, auditAgent(t, model, sink, nil, tracked("order", officina.Write), tracked("search", officina.Read)),
		&c, "Order it", officina.RunOptions{})

	want := []officina.ToolResult{
		{CallID: "c1", Content: "The call was not run: its attempt could not be recorded in the audit trail.", IsError: true},
		{CallID: "c2", Content: "done"},
	}
	if diff := cmp.Diff(want, results(c.Messages()[2])); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff([]string{"search"}, ran); diff != "" || result.Status != officina.Completed {
		t.Errorf("tools that ran mismatch (-want +got):\n%s\nresult = %v, want Completed", diff, result.Status)
	}
}

func TestRun_AUD02_AWriteRunsOnlyAfterItsAttemptIsRecorded(t *testing.T) {
	t.Parallel()
	sink := &memorySink{}
	var recorded bool
	order := handlerTool("order", officina.Write, func(context.Context, jsontext.Value) (string, error) {
		recorded = slices.ContainsFunc(sink.Entries(), func(e officina.AuditEntry) bool {
			return e.Kind == officina.AuditToolStarted && e.CallID == "c1"
		})
		return "ordered", nil
	})
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "order", `{}`)), officinatest.TextReply("Done."))

	run(t, auditAgent(t, model, sink, nil, order), nil, "Order it", officina.RunOptions{})

	if !recorded {
		t.Error("the write ran before its attempt was in the trail")
	}
}

func TestRun_AUD05_EVT03_SecretsNeverReachTheTrailEventsOrResultsAndLongTextIsCut(t *testing.T) {
	t.Parallel()
	const secret = `hunter<2>"`
	long := strings.Repeat("y", 5000)
	echo := handlerTool("echo", officina.Read, func(_ context.Context, input jsontext.Value) (string, error) {
		return "echo " + string(input) + " " + secret, nil
	})
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(
			// The secret as JSON escapes it for HTML, and as a JSON string escapes it otherwise.
			officinatest.ToolUseBlock("c1", "echo", `{"password":"hunter`+htmlEscaped("<2>")+`\"","again":"hunter<2>\""}`),
			officinatest.ToolUseBlock("c2", "echo", `{"text":"`+long+`"}`),
		),
		officinatest.TextReply("Your password is "+secret))
	sink := &memorySink{}
	var c officina.Conversation

	events, result := stream(t.Context(), t, auditAgent(t, model, sink, []string{secret}, echo), &c, "Hi",
		officina.RunOptions{}, func(officina.RunEvent) bool { return true })

	var seen []string
	for _, e := range events {
		switch e.(type) {
		case officina.ConversationAppended, officina.TextStreamed:
			// The two places a secret may appear, by design.
		default:
			seen = append(seen, fmt.Sprint(e))
		}
	}
	for _, e := range sink.Entries() {
		seen = append(seen, e.Input, e.Detail)
		if len(e.Input) > 4100 || len(e.Detail) > 4100 {
			t.Errorf("entry %d keeps %d and %d bytes, want at most about 4000", e.Sequence, len(e.Input), len(e.Detail))
		}
	}
	for _, r := range results(c.Messages()[2]) {
		seen = append(seen, r.Content)
	}
	seen = append(seen, result.Text)
	for _, s := range seen {
		if strings.Contains(s, "hunter") {
			t.Errorf("a secret reached %q", s)
		}
	}
	if !slices.ContainsFunc(sink.Entries(), func(e officina.AuditEntry) bool {
		return strings.HasSuffix(e.Input, "… [truncated: 5011 bytes]")
	}) {
		t.Error("no entry notes the long input's size")
	}
}

func TestJSONLinesSink_AUD04_AppendsOneLinePerEntryAndReadsBack(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "audit.jsonl")
	sink := officina.NewJSONLinesSink(path)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", `{}`)), officinatest.TextReply("Done."))
	agent := auditAgent(t, model, sink, nil, tool("search", "Searches."))

	run(t, agent, &officina.Conversation{ID: "s1"}, "Hi", officina.RunOptions{})
	run(t, agent, nil, "Again", officina.RunOptions{})

	file, err := os.Open(path)
	if err != nil {
		t.Fatalf("Open() error = %v", err)
	}
	t.Cleanup(func() { _ = file.Close() }) // Only read.
	var kinds []officina.AuditKind
	lines := bufio.NewScanner(file)
	for lines.Scan() {
		var e struct {
			Kind officina.AuditKind `json:"kind"`
		}
		if err := json.Unmarshal(lines.Bytes(), &e); err != nil {
			t.Fatalf("Unmarshal(%s) error = %v", lines.Bytes(), err)
		}
		kinds = append(kinds, e.Kind)
	}
	want := []officina.AuditKind{
		officina.AuditRunStarted, officina.AuditToolStarted, officina.AuditToolEnded, officina.AuditRunEnded,
		officina.AuditRunStarted, officina.AuditRunEnded,
	}
	if diff := cmp.Diff(want, kinds); diff != "" {
		t.Errorf("kinds mismatch (-want +got):\n%s", diff)
	}
}

func TestJSONLinesSink_AUD04_WritesEveryFieldAndReportsAFailure(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	entry := officina.AuditEntry{
		Time: time.Date(2026, 10, 9, 12, 0, 0, 0, time.UTC), Sequence: 3, Run: "r1", Conversation: "s1", Agent: "a",
		Kind: officina.AuditToolEnded, Tool: "search", CallID: "c1", Input: `{}`, Outcome: "ok", Detail: "found",
		Duration: 1500 * time.Millisecond, Usage: officina.Usage{Input: 1, Output: 2, CacheRead: 3, CacheWrite: 4},
	}

	err := officina.NewJSONLinesSink(filepath.Join(dir, "audit.jsonl")).Write(t.Context(), entry)
	missing := officina.NewJSONLinesSink(filepath.Join(dir, "no", "such", "dir.jsonl")).Write(t.Context(), entry)

	if err != nil {
		t.Fatalf("Write() error = %v", err)
	}
	data, err := os.ReadFile(filepath.Join(dir, "audit.jsonl"))
	if err != nil {
		t.Fatalf("ReadFile() error = %v", err)
	}
	want := `{"time":"2026-10-09T12:00:00Z","sequence":3,"run":"r1","conversation":"s1","agent":"a","kind":"ToolEnded",` +
		`"tool":"search","callId":"c1","input":"{}","outcome":"ok","detail":"found","duration":1500000000,` +
		`"usage":{"input":1,"output":2,"cacheRead":3,"cacheWrite":4}}` + "\n"
	if diff := cmp.Diff(want, string(data)); diff != "" {
		t.Errorf("file mismatch (-want +got):\n%s", diff)
	}
	if missing == nil {
		t.Error("Write() to a missing directory error = nil, want the failure")
	}
}
