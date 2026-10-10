package mcp_test

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/google/go-cmp/cmp"
	"go.opentelemetry.io/otel/attribute"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/mcp"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// The MCP tool source against the test kit's fake server, over stdio (a child process: this test binary) and
// Streamable HTTP (in process). Only the model, the server and the human are scripted.

func TestConnect_MCP01_AStdioServersToolsRunAndTheirResultsReachTheModel(t *testing.T) {
	t.Parallel()
	server := stdioServer(t, "")
	source := connect(t, server, mcp.AllowedTool{Name: "echo", Kind: officina.Read},
		mcp.AllowedTool{Name: "upper", Kind: officina.Read}, mcp.AllowedTool{Name: "fail"})
	model := officinatest.NewModel("scripted",
		// The two reads run at once, over the one connection.
		officinatest.ToolUseReply(call("c1", "fake__upper", `{"text":"hi"}`), call("c2", "fake__echo", `{"text":"there"}`),
			call("c3", "fake__fail", `{}`)),
		officinatest.TextReply("Done."))

	res := run(t, newAgent(t, model, source, server, officina.AgentOptions{}), nil, "Go.")

	if res.Status != officina.Completed {
		t.Fatalf("Status = %v (%s), want Completed", res.Status, res.Detail)
	}
	want := []officina.ToolResult{
		{CallID: "c1", Content: "HI"}, {CallID: "c2", Content: "there"},
		{CallID: "c3", Content: "it broke", IsError: true},
	}
	if diff := cmp.Diff(want, results(t, model, -1)); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
}

func TestConnect_MCP01_AnHTTPServersToolsRunWithTheCredentialTheHostGives(t *testing.T) {
	t.Parallel()
	fake, url := httpFake(t)
	server := httpServer(url, token)
	source := connect(t, server, allow("upper")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__upper", `{"text":"hi"}`)), officinatest.TextReply("Done."))

	res := run(t, newAgent(t, model, source, server, officina.AgentOptions{}), nil, "Go.")

	if res.Status != officina.Completed {
		t.Fatalf("Status = %v (%s), want Completed", res.Status, res.Detail)
	}
	if diff := cmp.Diff([]officina.ToolResult{{CallID: "c1", Content: "HI"}}, results(t, model, -1)); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff([]officinatest.MCPCall{{Tool: "upper", Arguments: `{"text":"hi"}`}}, fake.Calls()); diff != "" {
		t.Errorf("calls mismatch (-want +got):\n%s", diff)
	}
}

func TestConnect_MCP02_MCP03_OnlyAllowedToolsAppearNamedByServerAndToolAndAreWritesUnlessTheHostMarksThemRead(
	t *testing.T,
) {
	t.Parallel()
	_, url := httpFake(t, officinatest.MCPTool{Name: "delete_everything"})
	server := httpServer(url, token)

	source := connect(t, server, allow("echo", "upper")...)
	overridden := connect(t, server, mcp.AllowedTool{Name: "echo", Kind: officina.Write, NeedsApproval: true},
		mcp.AllowedTool{Name: "upper", Kind: officina.Read})

	type shape struct {
		Name          string
		Kind          officina.ToolKind
		NeedsApproval bool
		Source        bool
	}
	shapes := func(s *mcp.Source) []shape {
		var got []shape
		for _, tl := range s.Tools() {
			got = append(got, shape{tl.Name, tl.Kind, tl.NeedsApproval, tl.Source == s})
		}
		return got
	}
	// The fake server marks echo read-only; the annotation is not trusted.
	want := []shape{{"fake__echo", officina.Write, false, true}, {"fake__upper", officina.Write, false, true}}
	if diff := cmp.Diff(want, shapes(source)); diff != "" {
		t.Errorf("tools mismatch (-want +got):\n%s", diff)
	}
	want = []shape{{"fake__echo", officina.Write, true, true}, {"fake__upper", officina.Read, false, true}}
	if diff := cmp.Diff(want, shapes(overridden)); diff != "" {
		t.Errorf("overridden tools mismatch (-want +got):\n%s", diff)
	}

	// The model is offered only the allowed tools, as the server lists them.
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	run(t, newAgent(t, model, source, server, officina.AgentOptions{}), nil, "Hi.")
	offered := model.Requests()[0].Tools
	var names []string
	for _, tl := range offered {
		names = append(names, tl.Name)
	}
	if diff := cmp.Diff([]string{"fake__echo", "fake__upper"}, names); diff != "" {
		t.Errorf("offered tools mismatch (-want +got):\n%s", diff)
	}
	if offered[0].Description != "The echo tool." ||
		string(offered[0].InputSchema) != `{"type":"object","properties":{"text":{"type":"string"}}}` {
		t.Errorf("offered echo = %q, %s; want the server's description and schema", offered[0].Description,
			offered[0].InputSchema)
	}
}

func TestConnect_MCP03_AnAllowedToolTheServerLacksFailsTheConnectionClearly(t *testing.T) {
	t.Parallel()
	_, url := httpFake(t)

	_, err := mcp.Connect(t.Context(), httpServer(url, token), allow("echo", "write_file"))

	want := `mcp server "fake" has no tool "write_file"; it has: echo, upper`
	if err == nil || err.Error() != want {
		t.Errorf("Connect() error = %v, want %s", err, want)
	}
}

func TestRun_MCP03_CTX04_TheToolListIsReadOnceAndPinnedForTheConversation(t *testing.T) {
	t.Parallel()
	fake, url := httpFake(t)
	server := httpServer(url, token)
	source := connect(t, server, allow("echo")...)
	model := officinatest.NewModel("scripted",
		officinatest.TextReply("One."), officinatest.TextReply("Two."), officinatest.TextReply("Three."))
	agent := newAgent(t, model, source, server, officina.AgentOptions{})
	c := &officina.Conversation{}
	run(t, agent, c, "First.")

	// The server changes its tools and even loses its connection, which the next run restores.
	fake.SetTools(officinatest.MCPTool{Name: "echo", Description: "A new description."})
	run(t, agent, c, "Second.")
	fake.SetDown(true)
	if res := run(t, agent, c, "Down."); res.Failure != officina.ToolSourceUnavailable {
		t.Errorf("Failure = %v, want ToolSourceUnavailable", res.Failure)
	}
	fake.SetDown(false)
	run(t, agent, c, "Third.")

	requests := model.Requests()
	if len(requests) != 3 {
		t.Fatalf("the model got %d requests, want 3", len(requests))
	}
	for i, r := range requests {
		if len(r.Tools) != 1 || r.Tools[0].Description != "The echo tool." {
			t.Errorf("request %d offers %+v, want the pinned echo tool", i, r.Tools)
		}
	}
	if err := officinatest.CheckPrefix(requests); err != nil {
		t.Errorf("CheckPrefix() = %v", err)
	}

	// A source connected now reads the changed list: its agent has another prefix, so it starts a new conversation.
	changed := connect(t, server, allow("echo")...)
	rebuilt := newAgent(t, officinatest.NewModel("scripted", officinatest.TextReply("Four.")), changed, server,
		officina.AgentOptions{})
	if res := run(t, rebuilt, c, "Fourth."); res.Failure != officina.PrefixMismatch {
		t.Errorf("Failure = %v, want PrefixMismatch", res.Failure)
	}
}

func TestRun_MCP02_AnMCPToolGoesThroughValidationApprovalAuditTruncationAndEvents(t *testing.T) {
	t.Parallel()
	fake, url := httpFake(t, officinatest.MCPTool{Name: "big", Handler: func(jsontext.Value) (string, error) {
		return strings.Repeat("x", 70_000), nil
	}})
	server := httpServer(url, token)
	source := connect(t, server, mcp.AllowedTool{Name: "upper", NeedsApproval: true}, mcp.AllowedTool{Name: "big"})
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__upper", `{"text":5}`)),
		officinatest.ToolUseReply(call("c2", "fake__upper", `{"text":"no"}`)),
		officinatest.ToolUseReply(call("c3", "fake__upper", `{"text":"yes"}`), call("c4", "fake__big", `{}`)),
		officinatest.TextReply("Done."))
	approver := officinatest.NewApprover(officina.Approval{Reason: "not now"}, officina.Approval{Approved: true})
	trail := &sink{}
	spans := tracetest.NewInMemoryExporter()
	traces := sdktrace.NewTracerProvider(sdktrace.WithSyncer(spans))
	t.Cleanup(func() { _ = traces.Shutdown(context.Background()) }) // In memory: it cannot fail.
	agent := newAgent(t, model, source, server, officina.AgentOptions{
		Approver: approver, AuditSink: trail, TracerProvider: traces,
	})

	events, result := agent.Stream(t.Context(), nil, "Go.", officina.RunOptions{})
	var finished []string
	asked := 0
	for e := range events {
		switch e := e.(type) {
		case officina.ToolCallFinished:
			finished = append(finished, e.Call.ID)
		case officina.ApprovalAsked:
			asked++
		}
	}
	if _, err := result(); err != nil {
		t.Fatalf("result() error = %v", err)
	}

	// Invalid input and a denied call never reach the server; the approved one does.
	wantCalls := []officinatest.MCPCall{{Tool: "upper", Arguments: `{"text":"yes"}`}, {Tool: "big", Arguments: `{}`}}
	if diff := cmp.Diff(wantCalls, fake.Calls()); diff != "" {
		t.Errorf("calls mismatch (-want +got):\n%s", diff)
	}
	invalid, denied, last := results(t, model, 1)[0], results(t, model, 2)[0], results(t, model, 3)
	if !invalid.IsError || !strings.HasPrefix(invalid.Content, "The input does not match the tool's schema:") {
		t.Errorf("invalid input's result = %+v, want a schema error", invalid)
	}
	if diff := cmp.Diff(officina.ToolResult{CallID: "c2", Content: "The call was denied: not now", IsError: true},
		denied); diff != "" {
		t.Errorf("denied result mismatch (-want +got):\n%s", diff)
	}
	if last[0].Content != "YES" || last[0].IsError {
		t.Errorf("approved result = %+v, want YES", last[0])
	}
	if !strings.HasSuffix(last[1].Content, "[Truncated: the result had 70000 bytes; only the first 64000 are shown.]") {
		t.Errorf("big result ends %q, want the truncation note", last[1].Content[len(last[1].Content)-80:])
	}
	if diff := cmp.Diff([]string{"c1", "c2", "c3", "c4"}, finished); diff != "" {
		t.Errorf("finished calls mismatch (-want +got):\n%s", diff)
	}
	if asked != 2 {
		t.Errorf("approvals asked = %d, want 2", asked)
	}
	var c3 []officina.AuditKind
	for _, e := range trail.all() {
		if e.CallID == "c3" && (e.Kind == officina.AuditToolStarted || e.Kind == officina.AuditToolEnded) {
			c3 = append(c3, e.Kind)
		}
	}
	if diff := cmp.Diff([]officina.AuditKind{officina.AuditToolStarted, officina.AuditToolEnded}, c3); diff != "" {
		t.Errorf("c3's audit mismatch (-want +got):\n%s", diff)
	}
	toolSpans := 0
	for _, s := range spans.GetSpans() {
		if !strings.HasPrefix(s.Name, "execute_tool") {
			continue
		}
		toolSpans++
		if !slices.Contains(s.Attributes, attribute.String("officina.tool.source", "fake")) {
			t.Errorf("span %s has %v, want the source fake", s.Name, s.Attributes)
		}
	}
	if toolSpans != 4 {
		t.Errorf("tool spans = %d, want 4", toolSpans)
	}
}

func TestRun_MCP04_AServerDownAtTheStartOfARunFailsItClearlyAndTheNextRunReconnects(t *testing.T) {
	t.Parallel()
	fake, url := httpFake(t)
	server := httpServer(url, token)
	source := connect(t, server, allow("echo")...)
	model := officinatest.NewModel("scripted", officinatest.TextReply("Back."))
	trail := &sink{}
	agent := newAgent(t, model, source, server, officina.AgentOptions{AuditSink: trail})
	c := &officina.Conversation{}
	fake.SetDown(true)

	res := run(t, agent, c, "Hi.")

	if res.Status != officina.Failed || res.Failure != officina.ToolSourceUnavailable {
		t.Fatalf("result = %v %v, want Failed ToolSourceUnavailable", res.Status, res.Failure)
	}
	if want := `the tool source "fake" is not available: mcp server "fake" could not be reached: `; !strings.HasPrefix(
		res.Detail, want) {
		t.Errorf("Detail = %q, want it to start %q", res.Detail, want)
	}
	if len(model.Requests()) != 0 || len(c.Messages()) != 0 {
		t.Errorf("the failed run made %d requests and appended %d messages, want none", len(model.Requests()),
			len(c.Messages()))
	}

	fake.SetDown(false)
	if res := run(t, agent, c, "Hi again."); res.Status != officina.Completed {
		t.Errorf("next run = %v (%s), want Completed", res.Status, res.Detail)
	}
	// The host's first connection, the loss, the failed attempt, and the new connection.
	want := []string{"fake connected", "fake disconnected", "fake failed", "fake connected"}
	if diff := cmp.Diff(want, trail.sourceChanges()); diff != "" {
		t.Errorf("source changes mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_MCP04_AnHTTPServerThatFailsMidRunGivesErrorResultsAndTheRunGoesOn(t *testing.T) {
	t.Parallel()
	var fake *officinatest.MCPServer
	fake, url := httpFake(t, officinatest.MCPTool{Name: "drop", Handler: func(jsontext.Value) (string, error) {
		fake.SetDown(true)
		return "never sent", nil
	}})
	server := httpServer(url, token)
	source := connect(t, server, allow("drop", "echo")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__drop", `{}`)),
		officinatest.ToolUseReply(call("c2", "fake__echo", `{"text":"anyone?"}`)),
		officinatest.TextReply("The file server is down."))
	trail := &sink{}

	res := run(t, newAgent(t, model, source, server, officina.AgentOptions{AuditSink: trail}), nil, "Go.")

	if res.Status != officina.Completed {
		t.Fatalf("Status = %v (%s), want Completed", res.Status, res.Detail)
	}
	first, second := results(t, model, 1)[0], results(t, model, 2)[0]
	if !first.IsError || !strings.HasPrefix(first.Content, `mcp server "fake" could not be reached: `) {
		t.Errorf("first result = %+v, want unreachable", first)
	}
	if want := `mcp server "fake" is not connected: mcp server "fake" could not be reached: `; !second.IsError ||
		!strings.HasPrefix(second.Content, want) {
		t.Errorf("second result = %+v, want not connected", second)
	}
	if diff := cmp.Diff([]string{"fake connected", "fake disconnected"}, trail.sourceChanges()); diff != "" {
		t.Errorf("source changes mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_MCP04_AStdioServerThatExitsMidRunGivesAnErrorResultAndTheNextRunStartsItAgain(t *testing.T) {
	t.Parallel()
	server := stdioServer(t, "")
	source := connect(t, server, allow("crash", "echo")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__crash", `{}`)), officinatest.TextReply("It crashed."),
		officinatest.ToolUseReply(call("c2", "fake__echo", `{"text":"back"}`)), officinatest.TextReply("It is back."))
	trail := &sink{}
	agent := newAgent(t, model, source, server, officina.AgentOptions{AuditSink: trail})
	c := &officina.Conversation{}

	if res := run(t, agent, c, "Crash it."); res.Status != officina.Completed {
		t.Fatalf("Status = %v (%s), want Completed", res.Status, res.Detail)
	}
	want := officina.ToolResult{CallID: "c1", Content: `mcp server "fake" could not be reached: it closed its connection`,
		IsError: true}
	if diff := cmp.Diff([]officina.ToolResult{want}, results(t, model, 1)); diff != "" {
		t.Errorf("crash result mismatch (-want +got):\n%s", diff)
	}

	if res := run(t, agent, c, "Again."); res.Status != officina.Completed {
		t.Fatalf("Status = %v (%s), want Completed", res.Status, res.Detail)
	}
	if diff := cmp.Diff([]officina.ToolResult{{CallID: "c2", Content: "back"}}, results(t, model, -1)); diff != "" {
		t.Errorf("result after restart mismatch (-want +got):\n%s", diff)
	}
	want2 := []string{"fake connected", "fake disconnected", "fake connected"}
	if diff := cmp.Diff(want2, trail.sourceChanges()); diff != "" {
		t.Errorf("source changes mismatch (-want +got):\n%s", diff)
	}
}

func TestConnect_MCP04_AStdioServerThatCannotStartFailsToConnectClearly(t *testing.T) {
	t.Parallel()

	_, err := mcp.Connect(t.Context(), mcp.Server{Name: "fake", Command: "no-such-program-officina"}, nil)

	if want := `mcp server "fake" could not be started (no-such-program-officina): `; err == nil ||
		!strings.HasPrefix(err.Error(), want) {
		t.Errorf("Connect() error = %v, want it to start %q", err, want)
	}
}

func TestConnect_MCP04_AStdioServerThatExitsWhileConnectingIsReportedWithWhatItSaidOnItsErrorOutput(t *testing.T) {
	t.Parallel()

	_, err := mcp.Connect(t.Context(), stdioServer(t, "", "complain"), allow("echo"))

	if want := "it closed its connection: configuration file missing"; err == nil || !strings.HasSuffix(err.Error(), want) {
		t.Errorf("Connect() error = %v, want it to end %q", err, want)
	}
}

func TestConnect_MCP04_AConnectCancelledWhileTheServerStartsLeavesNoServerRunning(t *testing.T) {
	t.Parallel()
	pidFile := filepath.Join(t.TempDir(), "fake.pid")
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	type outcome struct {
		source *mcp.Source
		err    error
	}
	var (
		got        outcome
		connecting sync.WaitGroup
	)
	server := stdioServer(t, "", "silent", pidFile)
	connecting.Go(func() {
		got.source, got.err = mcp.Connect(ctx, server, nil)
	})

	// The server has started and will never answer: the connect is cancelled while it waits.
	proc := waitForProcess(t, pidFile)
	cancel()
	connecting.Wait()

	if got.source != nil || !errors.Is(got.err, context.Canceled) {
		t.Errorf("Connect() = %v, %v; want context.Canceled", got.source, got.err)
	}
	if !proc.exited() {
		t.Errorf("the server process %d outlived the cancelled connect", proc.pid)
	}
}

func TestClose_MCP01_ClosingStopsTheServerAndItsToolsThenGiveErrorResults(t *testing.T) {
	t.Parallel()
	server := stdioServer(t, "")
	source := connect(t, server, allow("pid", "echo")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__pid", `{}`)), officinatest.TextReply("Got it."))
	agent := newAgent(t, model, source, server, officina.AgentOptions{})
	run(t, agent, nil, "Your pid?")
	pid, err := strconv.Atoi(results(t, model, 1)[0].Content)
	if err != nil {
		t.Fatalf("the pid tool answered %q", results(t, model, 1)[0].Content)
	}
	proc := watch(t, pid)

	source.Close()

	if !proc.exited() {
		t.Errorf("the server process %d outlived Close", pid)
	}
	echo := source.Tools()[1]
	if _, err := echo.Handler(t.Context(), jsontext.Value(`{"text":"hi"}`)); err == nil ||
		err.Error() != `mcp server "fake" is not connected` {
		t.Errorf("a closed source's tool error = %v, want not connected", err)
	}
	if res := run(t, agent, nil, "Again."); res.Failure != officina.ToolSourceUnavailable {
		t.Errorf("a run after Close = %v %v, want ToolSourceUnavailable", res.Status, res.Failure)
	}
}

// waitForProcess waits for a fake server to write its process id to file, and watches the process.
func waitForProcess(t *testing.T, file string) process {
	t.Helper()
	return waitForProcesses(t, file)[0]
}

// waitForProcesses waits for a fake server to write process ids to file, and watches the processes, which are
// running until the test stops them.
func waitForProcesses(t *testing.T, file string) []process {
	t.Helper()
	for {
		if data, err := os.ReadFile(file); err == nil {
			var procs []process
			for field := range strings.FieldsSeq(string(data)) {
				pid, err := strconv.Atoi(field)
				if err != nil {
					t.Fatalf("the pid file holds %q", data)
				}
				procs = append(procs, watch(t, pid))
			}
			return procs
		}
		select {
		case <-t.Context().Done():
			t.Fatal("the fake server never started")
		case <-time.After(10 * time.Millisecond):
		}
	}
}

// waitExited waits for the process to have exited and been waited for: a process the server started is waited
// for by the system once its parent has gone, which takes a moment.
func waitExited(t *testing.T, p process) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for !p.exited() {
		if time.Now().After(deadline) {
			t.Errorf("process %d is still running", p.pid)
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
}

func TestRun_EVT03_MCPCredentialsNeverReachTheModelEventsAuditOrErrors(t *testing.T) {
	t.Parallel()
	server := stdioServer(t, token)
	source := connect(t, server, allow("token")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__token", `{}`)), officinatest.TextReply("Done."))
	trail := &sink{}
	agent := newAgent(t, model, source, server, officina.AgentOptions{AuditSink: trail})

	events, result := agent.Stream(t.Context(), nil, "Go.", officina.RunOptions{})
	var shown strings.Builder
	for e := range events {
		fmt.Fprintf(&shown, "%+v\n", e)
	}
	if _, err := result(); err != nil {
		t.Fatalf("result() error = %v", err)
	}

	if diff := cmp.Diff([]officina.ToolResult{{CallID: "c1", Content: "my token is [redacted]"}},
		results(t, model, -1)); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
	recorded := fmt.Sprintf("%+v", trail.all())
	if strings.Contains(shown.String(), token) || strings.Contains(recorded, token) {
		t.Errorf("the token reached the events or the trail:\n%s\n%s", shown.String(), recorded)
	}

	// A server that refuses the credential says so without it.
	_, url := httpFake(t)
	_, err := mcp.Connect(t.Context(), httpServer(url, "wrong-token-19"), allow("echo"))
	if err == nil || !strings.Contains(err.Error(), "401") || strings.Contains(err.Error(), "wrong-token-19") {
		t.Errorf("Connect() error = %v, want a 401 without the credential", err)
	}
}

func TestRun_EVT03_AServersCredentialIsRedactedFromItsResultsEvenWhenTheHostDidNotAddItToTheSecrets(t *testing.T) {
	t.Parallel()
	server := stdioServer(t, token)
	source := connect(t, server, allow("token")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__token", `{}`)), officinatest.TextReply("Done."))
	agent, err := officina.NewAgent(model, "You use tools.", officina.AgentOptions{Tools: source.Tools()})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	run(t, agent, nil, "Go.")

	if diff := cmp.Diff([]officina.ToolResult{{CallID: "c1", Content: "my token is [redacted]"}},
		results(t, model, -1)); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_MCP04_AnHTTPSessionTheServerEndsMidRunGivesErrorResultsAndTheNextRunStartsANewOne(t *testing.T) {
	t.Parallel()
	var fake *officinatest.MCPServer
	fake, url := httpFake(t, officinatest.MCPTool{Name: "restart", Handler: func(jsontext.Value) (string, error) {
		fake.EndSession()
		return "restarting", nil
	}})
	server := httpServer(url, token)
	source := connect(t, server, allow("restart", "echo")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__restart", `{}`)),
		officinatest.ToolUseReply(call("c2", "fake__echo", `{"text":"hello?"}`)),
		officinatest.TextReply("The server restarted."),
		officinatest.ToolUseReply(call("c3", "fake__echo", `{"text":"hello again"}`)),
		officinatest.TextReply("Back."))
	agent := newAgent(t, model, source, server, officina.AgentOptions{})

	run(t, agent, nil, "Go.")
	next := run(t, agent, nil, "Again.")

	if got := results(t, model, 1)[0].Content; got != "restarting" {
		t.Errorf("restart's result = %q, want restarting", got)
	}
	if ended := results(t, model, 2)[0]; !ended.IsError || !strings.HasSuffix(ended.Content, "the server ended the session") {
		t.Errorf("result after the session ended = %+v, want an error saying so", ended)
	}
	if next.Status != officina.Completed || results(t, model, 4)[0].Content != "hello again" {
		t.Errorf("next run = %v, %+v; want Completed with hello again", next.Status, results(t, model, 4))
	}
}

func TestConnect_MCP04_AServerThatSpeaksAnotherProtocolVersionIsRefused(t *testing.T) {
	t.Parallel()
	fake := officinatest.NewMCPServer(officinatest.MCPTool{Name: "echo"})
	fake.ProtocolVersion = "1999-01-01"
	_, url := httpFakeOf(t, fake)

	_, err := mcp.Connect(t.Context(), mcp.Server{Name: "fake", URL: url}, allow("echo"))

	if err == nil || !strings.Contains(err.Error(), `speaks protocol "1999-01-01"`) {
		t.Errorf("Connect() error = %v, want the version refused", err)
	}
}

func TestRun_MCP02_AnEmptyCredentialIsNotRedactedAndDoesNotBreakTheCalls(t *testing.T) {
	t.Parallel()
	_, url := httpFake(t)
	server := httpServer(url, token)
	server.Header.Set("X-Trace", "")
	source := connect(t, server, allow("echo")...)
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(call("c1", "fake__echo", `{"text":"plain text"}`)), officinatest.TextReply("Done."))

	run(t, newAgent(t, model, source, server, officina.AgentOptions{}), nil, "Go.")

	if got := results(t, model, -1)[0].Content; got != "plain text" {
		t.Errorf("result = %q, want plain text", got)
	}
}

func TestNewAgent_MCP03_AnAllowedToolWhoseSchemaTheCoreCannotUseFailsWithWhy(t *testing.T) {
	t.Parallel()
	_, url := httpFake(t, officinatest.MCPTool{
		Name: "odd", InputSchema: `{"type":"object","properties":{"when":{"type":"datetime"}}}`,
	})
	source := connect(t, httpServer(url, token), allow("odd")...)

	_, err := officina.NewAgent(officinatest.NewModel("scripted"), "You use tools.",
		officina.AgentOptions{Tools: source.Tools()})

	if want := `new agent: tool "fake__odd": `; err == nil || !strings.HasPrefix(err.Error(), want) {
		t.Errorf("NewAgent() error = %v, want it to start %q", err, want)
	}
}

func TestConnect_MCP03_AServerThatCannotPrefixToolNamesOrIsNotOneKindIsRefused(t *testing.T) {
	t.Parallel()
	const (
		badName = "the name must be ASCII letters"
		notOne  = "give either a command or a URL"
		// A usable server, which is not there.
		absent = "could not be reached"
	)
	tests := []struct {
		server mcp.Server
		want   string
	}{
		{mcp.Server{Name: "files server", URL: "http://127.0.0.1:1/mcp"}, badName},
		{mcp.Server{Name: "files.server", URL: "http://127.0.0.1:1/mcp"}, badName},
		{mcp.Server{Name: "", URL: "http://127.0.0.1:1/mcp"}, badName},
		{mcp.Server{Name: "café", URL: "http://127.0.0.1:1/mcp"}, badName},
		{mcp.Server{Name: "azAZ09_-", URL: "http://127.0.0.1:1/mcp"}, absent},
		{mcp.Server{Name: "fake"}, notOne},
		{mcp.Server{Name: "fake", Command: "server", URL: "http://127.0.0.1:1/mcp"}, notOne},
	}
	for _, tt := range tests {
		t.Run(tt.server.Name, func(t *testing.T) {
			t.Parallel()
			if _, err := mcp.Connect(t.Context(), tt.server, nil); err == nil || !strings.Contains(err.Error(), tt.want) {
				t.Errorf("Connect(%+v) error = %v, want one saying %q", tt.server, err, tt.want)
			}
		})
	}
}

func TestServer_EVT03_SecretsAreTheValuesOfTheEnvironmentAndHeaders(t *testing.T) {
	t.Parallel()
	server := mcp.Server{
		Env:    []string{"TOKEN=abc", "EMPTY=", "NOVALUE"},
		Header: map[string][]string{"Authorization": {"Bearer xyz"}, "X-Empty": {""}},
	}

	if diff := cmp.Diff([]string{"abc", "Bearer xyz"}, server.Secrets()); diff != "" {
		t.Errorf("Secrets() mismatch (-want +got):\n%s", diff)
	}
}
