package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"fmt"
	"strings"
	"sync"
	"testing"
	"testing/synctest"

	"github.com/google/go-cmp/cmp"
	"github.com/google/go-cmp/cmp/cmpopts"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func ok(context.Context, jsontext.Value) (string, error) { return "ok", nil }

// panickingApprover is a host's approver with a bug.
type panickingApprover struct{}

func (panickingApprover) Approve(context.Context, officina.Tool, officina.ToolCall) (officina.Approval, error) {
	panic("no console")
}

func TestRun_AGT02_TheRunCallsToolsThenTheModelAgainUntilItEnds(t *testing.T) {
	t.Parallel()
	search := handlerTool("search", officina.Read, func(_ context.Context, input jsontext.Value) (string, error) {
		return "found " + string(input), nil
	})
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", `{"q":"Emma"}`)),
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c2", "search", `{"q":"Persuasion"}`)),
		officinatest.TextReply("Both are in stock."))
	var c officina.Conversation

	result := run(t, newAgent(t, model, search), &c, "Do you have Emma and Persuasion?", officina.RunOptions{})

	if diff := cmp.Diff(officina.Result{Status: officina.Completed, Text: "Both are in stock."}, result, outcome()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	messages := c.Messages()
	want := []officina.Role{
		officina.User, officina.Assistant, officina.User, officina.Assistant, officina.User, officina.Assistant,
	}
	if diff := cmp.Diff(want, roles(messages)); diff != "" {
		t.Errorf("roles mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff([]officina.ToolResult{{CallID: "c2", Content: `found {"q":"Persuasion"}`}},
		results(messages[4])); diff != "" {
		t.Errorf("second results mismatch (-want +got):\n%s", diff)
	}
	if err := officinatest.CheckConversation(messages); err != nil {
		t.Errorf("CheckConversation() = %v", err)
	}
	if err := officinatest.CheckPrefix(model.Requests()); err != nil {
		t.Errorf("CheckPrefix() = %v", err)
	}
}

func TestRun_TOOL02_TOOL04_TOOL05_FailuresComeBackAsErrorResultsAndTheRunContinues(t *testing.T) {
	t.Parallel()
	schema := jsontext.Value(`{"type":"object","properties":{"isbn":{"type":"string"}},"required":["isbn"]}`)
	tests := []struct {
		name     string
		tool     officina.Tool
		input    string
		approver officina.Approver
		want     string
	}{
		{"unknown tool", officina.Tool{Name: "other"}, `{}`, nil, `There is no tool named "search".`},
		{"input not JSON", officina.Tool{}, `{"isbn":`, nil, "The input is not valid JSON: "},
		{
			"invalid input", officina.Tool{}, `{"isbn":7,"x":1}`, nil,
			"The input does not match the tool's schema:\n/isbn: must be string",
		},
		{
			"failing handler", officina.Tool{Handler: func(context.Context, jsontext.Value) (string, error) {
				return "", errors.New("not enough stock: 2 left")
			}}, `{"isbn":"1"}`, nil, "not enough stock: 2 left",
		},
		{
			"panicking handler", officina.Tool{Handler: func(context.Context, jsontext.Value) (string, error) {
				panic("the stock table is gone")
			}}, `{"isbn":"1"}`, nil, "The tool failed: the stock table is gone",
		},
		{
			"denied", officina.Tool{NeedsApproval: true}, `{"isbn":"1"}`,
			officinatest.NewApprover(officina.Approval{Reason: "not today"}), "The call was denied: not today",
		},
		{
			"denied without a reason", officina.Tool{NeedsApproval: true}, `{"isbn":"1"}`,
			officinatest.NewApprover(officina.Approval{}), "The call was denied.",
		},
		{
			"failing approver", officina.Tool{NeedsApproval: true}, `{"isbn":"1"}`, officinatest.NewApprover(),
			"The call was denied: asking for approval failed: scripted approver: request 1 has no answer left",
		},
		{
			"panicking approver", officina.Tool{NeedsApproval: true}, `{"isbn":"1"}`, panickingApprover{},
			"The call was denied: asking for approval failed: the approver panicked: no console",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			search := tt.tool
			if search.Name == "" {
				search.Name = "search"
			}
			search.InputSchema, search.Kind = schema, officina.Write
			if search.Handler == nil {
				search.Handler = ok
			}
			model := officinatest.NewModel("scripted",
				officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", tt.input)),
				officinatest.TextReply("Sorry."))
			agent, err := officina.NewAgent(model, instructions,
				officina.AgentOptions{Tools: []officina.Tool{search}, Approver: tt.approver})
			if err != nil {
				t.Fatalf("NewAgent() error = %v", err)
			}
			var c officina.Conversation

			result := run(t, agent, &c, "Order it", officina.RunOptions{})

			if result.Status != officina.Completed || result.Text != "Sorry." {
				t.Errorf("result = %+v, want Completed with the next reply", result)
			}
			got := results(c.Messages()[2])
			if len(got) != 1 || !got[0].IsError || !strings.HasPrefix(got[0].Content, tt.want) || got[0].CallID != "c1" {
				t.Errorf("results = %+v, want one error result for c1 starting %q", got, tt.want)
			}
		})
	}
}

func TestRun_GEN04_AnUnattendedRunDeniesApprovalRequiringCallsAndTellsTheModelWhy(t *testing.T) {
	t.Parallel()
	ran := false
	order := handlerTool("order", officina.Write, func(context.Context, jsontext.Value) (string, error) {
		ran = true
		return "ordered", nil
	})
	order.NeedsApproval = true
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "order", `{}`), officinatest.ToolUseBlock("c2", "search", `{}`)),
		officinatest.TextReply("I could not order it."))
	var c officina.Conversation

	result := run(t, newAgent(t, model, order, tool("search", "Searches.")), &c, "Order it", officina.RunOptions{})

	want := []officina.ToolResult{
		{CallID: "c1", Content: "The call needs approval, and this run is unattended, so it was denied.", IsError: true},
		{CallID: "c2", Content: "ok"},
	}
	if diff := cmp.Diff(want, results(c.Messages()[2])); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
	if ran || result.Status != officina.Completed {
		t.Errorf("the tool ran = %v and the run ended %v; want not run, and Completed", ran, result.Status)
	}
}

func TestRun_TOOL04_EVT01_AnApprovedCallRunsAndEachStepIsAnEvent(t *testing.T) {
	t.Parallel()
	const secret = `pa"ss`
	order := handlerTool("order", officina.Write, func(context.Context, jsontext.Value) (string, error) {
		return "ordered with " + secret, nil
	})
	order.NeedsApproval = true
	call := officinatest.ToolUseBlock("c1", "order", `{"key":"pa\"ss"}`)
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(call), officinatest.TextReply("Ordered."))
	approver := officinatest.NewApprover(officina.Approval{Approved: true})
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{
		Tools: []officina.Tool{order}, Approver: approver, Secrets: []string{secret},
	})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	events, result := stream(t.Context(), t, agent, nil, "Order it", officina.RunOptions{},
		func(officina.RunEvent) bool { return true })

	shown := officina.ToolCall{ID: "c1", Name: "order", Input: jsontext.Value(`{"key":"[redacted]"}`)}
	answer := officina.ToolResult{CallID: "c1", Content: "ordered with [redacted]"}
	want := []officina.RunEvent{
		officina.ConversationAppended{Message: officina.Message{Role: officina.User, Blocks: []officina.Block{{Text: "Order it"}}}},
		officina.ConversationAppended{Message: officina.Message{Role: officina.Assistant, Blocks: []officina.Block{call}}},
		officina.ToolCallStarted{Call: shown},
		officina.ApprovalAsked{Call: shown},
		officina.ApprovalAnswered{Call: shown, Approved: true},
		officina.ToolCallFinished{Call: shown, Result: answer},
		officina.ConversationAppended{Message: officina.Message{Role: officina.User, Blocks: []officina.Block{{ToolResult: &answer}}}},
		officina.TextStreamed{Text: "Ordered."},
		officina.ConversationAppended{Message: officina.Message{
			Role: officina.Assistant, Blocks: []officina.Block{officinatest.TextBlock("Ordered.")},
		}},
	}
	if diff := cmp.Diff(want, events); diff != "" {
		t.Errorf("events mismatch (-want +got):\n%s", diff)
	}
	if result.Text != "Ordered." {
		t.Errorf("result.Text = %q, want %q", result.Text, "Ordered.")
	}
	if asked := approver.Asked(); len(asked) != 1 || string(asked[0].Input) != `{"key":"pa\"ss"}` {
		t.Errorf("approver asked about %+v, want the call as the model wrote it", asked)
	}
}

func TestRun_TOOL03_ReadsOverlapAndWritesRunAloneInOrder(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		var (
			mu  sync.Mutex
			log []string
			// Each of the first two reads waits until both have started: run one after the other, they would
			// deadlock, which synctest reports.
			both sync.WaitGroup
		)
		both.Add(2)
		note := func(s string) {
			mu.Lock()
			defer mu.Unlock()
			log = append(log, s)
		}
		tracked := func(name string, kind officina.ToolKind, wait bool) officina.Tool {
			return handlerTool(name, kind, func(context.Context, jsontext.Value) (string, error) {
				note("start " + name)
				if wait {
					both.Done()
					both.Wait()
				}
				note("end " + name)
				return name, nil
			})
		}
		model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
			officinatest.ToolUseBlock("c1", "r1", `{}`), officinatest.ToolUseBlock("c2", "r2", `{}`),
			officinatest.ToolUseBlock("c3", "w1", `{}`), officinatest.ToolUseBlock("c4", "r3", `{}`),
			officinatest.ToolUseBlock("c5", "w2", `{}`),
		), officinatest.TextReply("Done."))
		agent := newAgent(t, model, tracked("r1", officina.Read, true), tracked("r2", officina.Read, true),
			tracked("w1", officina.Write, false), tracked("r3", officina.Read, false), tracked("w2", officina.Write, false))
		var c officina.Conversation

		run(t, agent, &c, "Go", officina.RunOptions{})

		if diff := cmp.Diff([]string{"start r1", "start r2"}, log[:2], cmpopts.SortSlices(func(a, b string) bool { return a < b })); diff != "" {
			t.Errorf("first starts mismatch (-want +got):\n%s", diff)
		}
		want := []string{"start w1", "end w1", "start r3", "end r3", "start w2", "end w2"}
		if diff := cmp.Diff(want, log[4:]); diff != "" {
			t.Errorf("log after the overlapping reads mismatch (-want +got):\n%s", diff)
		}
		var ids []string
		for _, r := range results(c.Messages()[2]) {
			ids = append(ids, r.Content)
		}
		if diff := cmp.Diff([]string{"r1", "r2", "w1", "r3", "w2"}, ids); diff != "" {
			t.Errorf("results mismatch (-want +got):\n%s", diff)
		}
	})
}

func TestRun_CTX06_ResultsOfOneReplyReturnInOneMessageInCallOrder(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		// The first call finishes last: only once the host has seen the second one finish.
		second := make(chan struct{})
		slow := handlerTool("slow", officina.Read, func(context.Context, jsontext.Value) (string, error) {
			<-second
			return "slow", nil
		})
		fast := handlerTool("fast", officina.Read, func(context.Context, jsontext.Value) (string, error) {
			return "fast", nil
		})
		model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
			officinatest.ToolUseBlock("c1", "slow", `{}`), officinatest.ToolUseBlock("c2", "fast", `{}`),
		), officinatest.TextReply("Done."))
		var c officina.Conversation

		events, _ := stream(t.Context(), t, newAgent(t, model, slow, fast), &c, "Go", officina.RunOptions{},
			func(e officina.RunEvent) bool {
				if f, ok := e.(officina.ToolCallFinished); ok && f.Call.ID == "c2" {
					close(second)
				}
				return true
			})

		var finished []string
		for _, e := range events {
			if f, ok := e.(officina.ToolCallFinished); ok {
				finished = append(finished, f.Call.ID)
			}
		}
		if diff := cmp.Diff([]string{"c2", "c1"}, finished); diff != "" {
			t.Errorf("finish order mismatch (-want +got):\n%s", diff)
		}
		want := []officina.ToolResult{{CallID: "c1", Content: "slow"}, {CallID: "c2", Content: "fast"}}
		if diff := cmp.Diff(want, results(c.Messages()[2])); diff != "" {
			t.Errorf("results mismatch (-want +got):\n%s", diff)
		}
		if got := len(c.Messages()); got != 4 {
			t.Errorf("conversation has %d messages, want 4: the results are one message", got)
		}
	})
}

func TestRun_TOOL06_ALongResultIsTruncatedAndTheModelIsTold(t *testing.T) {
	t.Parallel()
	// 63,999 bytes, then a 2-byte character that the cut must not split.
	long := strings.Repeat("x", 63_999) + strings.Repeat("é", 1_000)
	big := handlerTool("big", officina.Read, func(context.Context, jsontext.Value) (string, error) { return long, nil })
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "big", `{}`)), officinatest.TextReply("Done."))
	var c officina.Conversation

	run(t, newAgent(t, model, big), &c, "Go", officina.RunOptions{})

	got := results(c.Messages()[2])[0].Content
	want := strings.Repeat("x", 63_999) + "\n[Truncated: the result had 65999 bytes; only the first 64000 are shown.]"
	if got != want {
		t.Errorf("result = %q…%q (%d bytes), want %d bytes", got[:10], got[len(got)-80:], len(got), len(want))
	}
}

func TestRun_AGT05_CancellingDuringToolsKeepsFinishedResultsAndCancelsTheRest(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	t.Cleanup(cancel)
	ran := 0
	cancelling := handlerTool("cancel", officina.Write, func(context.Context, jsontext.Value) (string, error) {
		ran++
		cancel()
		return "done", nil
	})
	waiting := handlerTool("wait", officina.Write, func(ctx context.Context, _ jsontext.Value) (string, error) {
		ran++
		<-ctx.Done()
		return "", ctx.Err()
	})
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
		officinatest.ToolUseBlock("c1", "cancel", `{}`), officinatest.ToolUseBlock("c2", "wait", `{}`),
	), officinatest.TextReply("Unused."))
	var c officina.Conversation

	_, result := stream(ctx, t, newAgent(t, model, cancelling, waiting), &c, "Go", officina.RunOptions{},
		func(officina.RunEvent) bool { return true })

	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.Cancelled}, result, outcome()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	want := []officina.ToolResult{
		{CallID: "c1", Content: "done"},
		{CallID: "c2", Content: "The call was cancelled before it started.", IsError: true},
	}
	if diff := cmp.Diff(want, results(c.Messages()[2])); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
	if ran != 1 || len(model.Requests()) != 1 {
		t.Errorf("%d tools ran and the model got %d requests, want 1 and 1", ran, len(model.Requests()))
	}
	if err := officinatest.CheckConversation(c.Messages()); err != nil {
		t.Errorf("CheckConversation() = %v", err)
	}
}

func TestRun_AGT05_EVT01_AConsumerThatBreaksDuringToolsStopsThemAndKeepsAValidConversation(t *testing.T) {
	t.Parallel()
	waiting := handlerTool("wait", officina.Write, func(ctx context.Context, _ jsontext.Value) (string, error) {
		<-ctx.Done()
		return "", ctx.Err()
	})
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
		officinatest.ToolUseBlock("c1", "wait", `{}`), officinatest.ToolUseBlock("c2", "wait", `{}`),
	))
	var c officina.Conversation

	events, result := stream(t.Context(), t, newAgent(t, model, waiting), &c, "Go", officina.RunOptions{},
		func(e officina.RunEvent) bool {
			_, started := e.(officina.ToolCallStarted)
			return !started
		})

	if _, ok := events[len(events)-1].(officina.ToolCallStarted); !ok || result.Stop != officina.Cancelled {
		t.Errorf("last event = %T, result = %+v; want the run stopped at the event, Cancelled", events[len(events)-1], result)
	}
	// The first call may have started before the break reached it, or not; either way it was cancelled.
	got := results(c.Messages()[2])
	if len(got) != 2 || got[1] != (officina.ToolResult{
		CallID: "c2", Content: "The call was cancelled before it started.", IsError: true,
	}) || got[0].CallID != "c1" || !got[0].IsError || !strings.HasPrefix(got[0].Content, "The call was cancelled") {
		t.Errorf("results = %+v, want both calls cancelled", got)
	}
	if err := officinatest.CheckConversation(c.Messages()); err != nil {
		t.Errorf("CheckConversation() = %v", err)
	}
}

func TestRun_AGT03_ARunThatKeepsCallingToolsStopsAtTheIterationLimit(t *testing.T) {
	t.Parallel()
	replies := make([]officinatest.Reply, 30)
	for i := range replies {
		replies[i] = officinatest.ToolUseReply(officinatest.ToolUseBlock(fmt.Sprintf("c%d", i), "search", `{}`))
	}
	model := officinatest.NewModel("scripted", replies...)
	var c officina.Conversation

	result := run(t, newAgent(t, model, tool("search", "Searches.")), &c, "Go", officina.RunOptions{})

	if result.Status != officina.Stopped || result.Stop != officina.IterationLimit || len(model.Requests()) != 25 {
		t.Errorf("result = %+v after %d requests, want Stopped(IterationLimit) after 25", result, len(model.Requests()))
	}
	if err := officinatest.CheckConversation(c.Messages()); err != nil {
		t.Errorf("CheckConversation() = %v", err)
	}
}
