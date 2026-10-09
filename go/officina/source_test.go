package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"strings"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// fakeSource is a tool source whose connections fail with err while it is set; each Connect reports a change.
type fakeSource struct {
	name string
	// onConnect runs at each Connect, before it answers.
	onConnect func(ctx context.Context)

	mu       sync.Mutex
	err      error
	connects int
	changes  []officina.SourceChange
}

func (s *fakeSource) Name() string { return s.name }

func (s *fakeSource) Connect(ctx context.Context) error {
	if s.onConnect != nil {
		s.onConnect(ctx)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.connects++
	if ctx.Err() != nil {
		return ctx.Err()
	}
	if s.err != nil {
		s.changes = append(s.changes, officina.SourceChange{State: officina.SourceFailed, Detail: s.err.Error()})
		return s.err
	}
	s.changes = append(s.changes, officina.SourceChange{State: officina.SourceConnected})
	return nil
}

func (s *fakeSource) Changes() []officina.SourceChange {
	s.mu.Lock()
	defer s.mu.Unlock()
	changes := s.changes
	s.changes = nil
	return changes
}

// lose reports a lost connection, as a source does when its server goes away.
func (s *fakeSource) lose(reason string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.changes = append(s.changes, officina.SourceChange{State: officina.SourceLost, Detail: reason})
}

func (s *fakeSource) connected() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.connects
}

// sourceTool returns a write tool of source that runs handler.
func sourceTool(name string, source officina.ToolSource,
	handler func(ctx context.Context, input jsontext.Value) (string, error),
) officina.Tool {
	t := handlerTool(name, officina.Write, handler)
	t.Source = source
	return t
}

// sourceEntries returns the tool source entries of entries, as "<source> <outcome> <detail>".
func sourceEntries(entries []officina.AuditEntry) []string {
	var got []string
	for _, e := range entries {
		if e.Kind == officina.AuditToolSource {
			got = append(got, e.Tool+" "+e.Outcome+" "+e.Detail)
		}
	}
	return got
}

func TestRun_MCP04_ASourceThatCannotConnectFailsTheRunBeforeAnyModelCall(t *testing.T) {
	t.Parallel()
	source := &fakeSource{name: "files", err: errors.New("refused with key hunter2")}
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	trail := &memorySink{}
	ok := func(context.Context, jsontext.Value) (string, error) { return "ok", nil }
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools:     []officina.Tool{sourceTool("files__a", source, ok), sourceTool("files__b", source, ok)},
		AuditSink: trail, Secrets: []string{"hunter2"},
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	c := &officina.Conversation{}

	res := run(t, agent, c, "Hi.", officina.RunOptions{})

	want := officina.Result{Status: officina.Failed, Failure: officina.ToolSourceUnavailable,
		Detail: `the tool source "files" is not available: refused with key [redacted]`}
	if diff := cmp.Diff(want, res); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if len(model.Requests()) != 0 || len(c.Messages()) != 0 {
		t.Errorf("the run made %d requests and appended %d messages, want none", len(model.Requests()), len(c.Messages()))
	}
	// One source of two tools is connected once.
	if got := source.connected(); got != 1 {
		t.Errorf("Connect called %d times, want 1", got)
	}
	if diff := cmp.Diff([]string{"files failed refused with key [redacted]"}, sourceEntries(trail.Entries())); diff != "" {
		t.Errorf("source entries mismatch (-want +got):\n%s", diff)
	}

	source.mu.Lock()
	source.err = nil
	source.mu.Unlock()
	if res := run(t, agent, c, "Hi again.", officina.RunOptions{}); res.Status != officina.Completed {
		t.Errorf("the next run = %v (%s), want Completed", res.Status, res.Detail)
	}
}

func TestRun_AUD01_SourceChangesAreRecordedWhenTheRunConnectsAndAfterEachReplysCalls(t *testing.T) {
	t.Parallel()
	source := &fakeSource{name: "files"}
	lost := func(context.Context, jsontext.Value) (string, error) {
		source.lose("the server went away")
		return "", errors.New("the server went away")
	}
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "files__write", `{}`)),
		officinatest.TextReply("It failed."))
	trail := &memorySink{}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools:     []officina.Tool{sourceTool("files__write", source, lost), tool("local", "An application tool.")},
		AuditSink: trail,
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	res := run(t, agent, nil, "Write it.", officina.RunOptions{})

	if res.Status != officina.Completed {
		t.Fatalf("Status = %v (%s), want Completed", res.Status, res.Detail)
	}
	var kinds []officina.AuditKind
	for _, e := range trail.Entries() {
		kinds = append(kinds, e.Kind)
	}
	want := []officina.AuditKind{
		officina.AuditRunStarted, officina.AuditToolSource, officina.AuditToolStarted, officina.AuditToolEnded,
		officina.AuditToolSource, officina.AuditRunEnded,
	}
	if diff := cmp.Diff(want, kinds); diff != "" {
		t.Errorf("entry kinds mismatch (-want +got):\n%s", diff)
	}
	wantChanges := []string{"files connected ", "files disconnected the server went away"}
	if diff := cmp.Diff(wantChanges, sourceEntries(trail.Entries())); diff != "" {
		t.Errorf("source entries mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_AGT05_ARunCancelledWhileItsSourcesConnectStopsAsCancelled(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	source := &fakeSource{name: "files", onConnect: func(context.Context) { cancel() }}
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	ok := func(context.Context, jsontext.Value) (string, error) { return "ok", nil }
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools: []officina.Tool{sourceTool("files__a", source, ok)},
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	res, err := agent.Run(ctx, nil, "Hi.", officina.RunOptions{})

	if err != nil || res.Status != officina.Stopped || res.Stop != officina.Cancelled {
		t.Errorf("Run() = %+v, %v; want Stopped(Cancelled)", res, err)
	}
	if len(model.Requests()) != 0 {
		t.Errorf("the model got %d requests, want none", len(model.Requests()))
	}
}

func TestSourceState_AUD01_StatesPrintTheirNames(t *testing.T) {
	t.Parallel()
	got := []string{officina.SourceConnected.String(), officina.SourceFailed.String(), officina.SourceLost.String(),
		officina.SourceState(0).String(), officina.ToolSourceUnavailable.String()}
	want := []string{"SourceConnected", "SourceFailed", "SourceLost", "SourceState(0)", "ToolSourceUnavailable"}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("names mismatch (-want +got):\n%s", diff)
	}
}

// valueSource is a tool source whose type cannot be compared, as it holds a slice.
type valueSource struct {
	names []string
}

func (valueSource) Name() string                     { return "value" }
func (valueSource) Connect(context.Context) error    { return nil }
func (valueSource) Changes() []officina.SourceChange { return nil }

func TestNewAgent_AGT01_ASourceThatCannotBeComparedIsRefused(t *testing.T) {
	t.Parallel()
	ok := func(context.Context, jsontext.Value) (string, error) { return "ok", nil }
	source := valueSource{names: []string{"a"}}

	_, err := officina.NewAgent(officinatest.NewModel("scripted"), instructions, officina.AgentOptions{
		Tools: []officina.Tool{sourceTool("value__a", source, ok), sourceTool("value__b", source, ok)},
	})

	if want := "new agent: a tool source cannot be compared"; err == nil || !strings.HasPrefix(err.Error(), want) {
		t.Errorf("NewAgent() error = %v, want it to start %q", err, want)
	}
}
