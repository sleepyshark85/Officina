package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"fmt"
	"iter"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestRun_AGT02_MultiTurnRunCompletesWithText(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		officinatest.TextReply("Hello!"), officinatest.TextReply("Paris."), officinatest.TextReply("You're welcome."))
	agent := newAgent(t, model)
	var c officina.Conversation

	var texts []string
	for _, message := range []string{"Hi", "Capital of France?", "Thanks"} {
		texts = append(texts, run(t, agent, &c, message, officina.RunOptions{}).Text)
	}

	if diff := cmp.Diff([]string{"Hello!", "Paris.", "You're welcome."}, texts); diff != "" {
		t.Errorf("texts mismatch (-want +got):\n%s", diff)
	}
	var got []string
	for _, m := range c.Messages() {
		got = append(got, string(m.Role)+": "+m.Text())
	}
	want := []string{
		"user: Hi", "assistant: Hello!", "user: Capital of France?", "assistant: Paris.", "user: Thanks",
		"assistant: You're welcome.",
	}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("conversation mismatch (-want +got):\n%s", diff)
	}
	var sizes []int
	for _, req := range model.Requests() {
		sizes = append(sizes, len(req.Messages))
	}
	if diff := cmp.Diff([]int{1, 3, 5}, sizes); diff != "" {
		t.Errorf("request sizes mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_GEN02_GEN03_AgentOfModelAndInstructionsRunsStatelessly(t *testing.T) {
	t.Parallel()
	agent, err := officina.NewAgent(officinatest.NewModel("scripted", officinatest.TextReply("positive")),
		"Classify the sentiment.", officina.AgentOptions{})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	result := run(t, agent, nil, "I love it", officina.RunOptions{})

	if diff := cmp.Diff(officina.Result{Status: officina.Completed, Text: "positive"}, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_EVT01_AGT08_EventsStreamThenEachAppendThenResult(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
		officina.TextDelta{Text: "Hel"},
		officina.TextDelta{Text: "lo"},
		officina.BlockReceived{Block: officinatest.TextBlock("Hello")},
		officina.UsageReceived{Usage: officina.Usage{Input: 100, Output: 5, CacheRead: 2, CacheWrite: 900}},
		officina.UsageReceived{Usage: officina.Usage{Output: 7, CacheRead: 3, CacheWrite: 1}},
		officina.Finished{Reason: officina.FinishEnd},
	}})
	var c officina.Conversation

	events, result := stream(t.Context(), t, newAgent(t, model), &c, "Hi", officina.RunOptions{Context: "Today is Monday."},
		func(officina.RunEvent) bool { return true })

	want := []officina.RunEvent{
		officina.TextStreamed{Text: "Hel"},
		officina.TextStreamed{Text: "lo"},
		officina.UsageReported{Usage: officina.Usage{Input: 100, Output: 5, CacheRead: 2, CacheWrite: 900}},
		officina.UsageReported{Usage: officina.Usage{Output: 7, CacheRead: 3, CacheWrite: 1}},
		officina.ConversationAppended{Message: officina.Message{Role: officina.User, Blocks: []officina.Block{{Text: "Hi"}}}},
		officina.ConversationAppended{Message: officina.Message{
			Role: officina.Operator, Blocks: []officina.Block{{Text: "Today is Monday."}},
		}},
		officina.ConversationAppended{Message: officina.Message{
			Role: officina.Assistant, Blocks: []officina.Block{officinatest.TextBlock("Hello")},
		}},
	}
	if diff := cmp.Diff(want, events); diff != "" {
		t.Errorf("events mismatch (-want +got):\n%s", diff)
	}
	wantResult := officina.Result{
		Status: officina.Completed, Text: "Hello", Usage: officina.Usage{Input: 100, Output: 12, CacheRead: 5, CacheWrite: 901},
	}
	if diff := cmp.Diff(wantResult, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_CTX01_RequestHasSortedToolsInstructionsThenUserAndOperatorMessages(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("Good morning, Ana."))
	agent := newAgent(t, model, tool("search", "Searches."), tool("order", "Orders."))

	run(t, agent, nil, "Hi", officina.RunOptions{Context: "Date: 2026-10-05. Staff: Ana."})

	want := officina.Request{
		Tools:        []officina.Tool{tool("order", "Orders."), tool("search", "Searches.")},
		Instructions: instructions,
		Messages: []officina.Message{
			{Role: officina.User, Blocks: []officina.Block{{Text: "Hi"}}},
			{Role: officina.Operator, Blocks: []officina.Block{{Text: "Date: 2026-10-05. Staff: Ana."}}},
		},
	}
	if diff := cmp.Diff([]officina.Request{want}, model.Requests()); diff != "" {
		t.Errorf("requests mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_AGT06_ReplyBlocksAreAppendedExactlyAsReceived(t *testing.T) {
	t.Parallel()
	thinking := officina.Block{Raw: jsontext.Value(`{ "type":"thinking", "thinking":"caf\u00e9", "signature":"c2ln+/=" }`)}
	text := officinatest.TextBlock("Done.")
	model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: thinking}, officina.BlockReceived{Block: text}, officina.Finished{Reason: officina.FinishEnd},
	}})
	var c officina.Conversation

	result := run(t, newAgent(t, model), &c, "Go", officina.RunOptions{})

	if result.Text != "Done." {
		t.Errorf("result.Text = %q, want %q", result.Text, "Done.")
	}
	messages := c.Messages()
	if diff := cmp.Diff([]officina.Block{thinking, text}, messages[len(messages)-1].Blocks); diff != "" {
		t.Errorf("reply blocks mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_AGT03_EveryFinishReasonMapsToItsResultAndTheReplyIsKept(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name     string
		finished officina.Finished
		want     officina.Result
	}{
		{"end", officina.Finished{Reason: officina.FinishEnd}, officina.Result{Status: officina.Completed, Text: "partial"}},
		{
			"max tokens", officina.Finished{Reason: officina.FinishMaxTokens},
			officina.Result{Status: officina.Stopped, Stop: officina.OutputLimit},
		},
		{
			"refusal", officina.Finished{Reason: officina.FinishRefusal, Detail: "cyber"},
			officina.Result{Status: officina.Stopped, Stop: officina.Refusal, Detail: "cyber"},
		},
		{
			"context full", officina.Finished{Reason: officina.FinishContextFull},
			officina.Result{Status: officina.Stopped, Stop: officina.ContextFull},
		},
		{
			"tool use without calls", officina.Finished{Reason: officina.FinishToolUse},
			officina.Result{
				Status: officina.Failed, Failure: officina.UnexpectedStop,
				Detail: "the model stopped to use tools but called none",
			},
		},
		{
			"unknown", officina.Finished{Reason: officina.FinishUnknown, Detail: "pause_turn"},
			officina.Result{
				Status: officina.Failed, Failure: officina.UnexpectedStop,
				Detail: "the model stopped for a reason the run cannot act on: pause_turn",
			},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
				officina.TextDelta{Text: "partial"}, officina.BlockReceived{Block: officinatest.TextBlock("partial")}, tt.finished,
			}})
			var c officina.Conversation

			result := run(t, newAgent(t, model), &c, "Hi", officina.RunOptions{})

			if diff := cmp.Diff(tt.want, result); diff != "" {
				t.Errorf("result mismatch (-want +got):\n%s", diff)
			}
			if diff := cmp.Diff([]officina.Role{officina.User, officina.Assistant}, roles(c.Messages())); diff != "" {
				t.Errorf("roles mismatch (-want +got):\n%s", diff)
			}
		})
	}
}

func TestRun_AGT03_ARunWithoutAReplyFailsOrEndsAndAppendsNothing(t *testing.T) {
	t.Parallel()
	usage := officina.Usage{Input: 10}
	tests := []struct {
		name  string
		model officina.Model
		want  officina.Result
	}{
		{
			"model error after streaming",
			officinatest.NewModel("scripted", officinatest.Reply{
				Events: []officina.ModelEvent{officina.TextDelta{Text: "Par"}, officina.UsageReceived{Usage: usage}},
				Err:    errors.New("overloaded after retries"),
			}),
			officina.Result{Status: officina.Failed, Failure: officina.ModelError, Detail: "overloaded after retries", Usage: usage},
		},
		{
			"no finish reason",
			officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
				officina.BlockReceived{Block: officinatest.TextBlock("Hi")},
			}}),
			officina.Result{
				Status: officina.Failed, Failure: officina.ModelError, Detail: "the model's reply ended without a finish reason",
			},
		},
		{
			"no reply left",
			officinatest.NewModel("scripted"),
			officina.Result{
				Status: officina.Failed, Failure: officina.ModelError, Detail: "scripted model: request 1 has no reply left",
			},
		},
		{
			"end without content",
			officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
				officina.Finished{Reason: officina.FinishEnd},
			}}),
			officina.Result{Status: officina.Completed},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			var c officina.Conversation

			result := run(t, newAgent(t, tt.model), &c, "Hi", officina.RunOptions{Context: "Date: 2026-10-05."})

			if diff := cmp.Diff(tt.want, result); diff != "" {
				t.Errorf("result mismatch (-want +got):\n%s", diff)
			}
			if got := c.Messages(); len(got) != 0 {
				t.Errorf("conversation has %d messages, want none", len(got))
			}
		})
	}
}

func TestRun_AGT05_CancellingMidStreamAppendsNothingAndTheNextRequestIsValid(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
		officina.TextDelta{Text: "Par"}, officina.TextDelta{Text: "is"},
		officina.BlockReceived{Block: officinatest.TextBlock("Paris")}, officina.Finished{Reason: officina.FinishEnd},
	}}, officinatest.TextReply("Hello again."))
	agent := newAgent(t, model)
	var c officina.Conversation
	ctx, cancel := context.WithCancel(t.Context())
	t.Cleanup(cancel)
	opts := officina.RunOptions{Context: "Date: 2026-10-05."}

	events, result := stream(ctx, t, agent, &c, "Capital of France?", opts, func(e officina.RunEvent) bool {
		if _, ok := e.(officina.TextStreamed); ok {
			cancel()
		}
		return true
	})

	if diff := cmp.Diff([]officina.RunEvent{officina.TextStreamed{Text: "Par"}}, events); diff != "" {
		t.Errorf("events mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.Cancelled}, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if got := c.Messages(); len(got) != 0 {
		t.Errorf("conversation has %d messages, want none", len(got))
	}

	next := run(t, agent, &c, "Hi", opts)

	if next.Text != "Hello again." {
		t.Errorf("next run's text = %q, want %q", next.Text, "Hello again.")
	}
	wantRoles := []officina.Role{officina.User, officina.Operator, officina.Assistant}
	if diff := cmp.Diff(wantRoles, roles(c.Messages())); diff != "" {
		t.Errorf("roles mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_AGT05_ARunCancelledBeforeItStartsCallsNoModel(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	var c officina.Conversation

	result, err := newAgent(t, model).Run(ctx, &c, "Hi", officina.RunOptions{})
	if err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.Cancelled}, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if got := len(model.Requests()); got != 0 {
		t.Errorf("model got %d requests, want none", got)
	}
}

func TestRun_AGT05_CancellingStopsTheRunEvenIfTheModelIgnoresIt(t *testing.T) {
	t.Parallel()
	ignoring := funcModel(func(context.Context, officina.Request) iter.Seq2[officina.ModelEvent, error] {
		return func(yield func(officina.ModelEvent, error) bool) {
			for _, e := range officinatest.TextReply("Paris").Events {
				if !yield(e, nil) {
					return
				}
			}
		}
	})
	var c officina.Conversation
	ctx, cancel := context.WithCancel(t.Context())
	t.Cleanup(cancel)

	_, result := stream(ctx, t, newAgent(t, ignoring), &c, "Hi", officina.RunOptions{}, func(officina.RunEvent) bool {
		cancel()
		return true
	})

	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.Cancelled}, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if got := c.Messages(); len(got) != 0 {
		t.Errorf("conversation has %d messages, want none", len(got))
	}
}

// goroutineModel streams a reply from a goroutine it owns, and records whether it stopped and waited for it.
type goroutineModel struct {
	released chan struct{}
}

func (goroutineModel) Settings() string { return "goroutine" }

func (m goroutineModel) Stream(ctx context.Context, _ officina.Request) iter.Seq2[officina.ModelEvent, error] {
	return func(yield func(officina.ModelEvent, error) bool) {
		ctx, cancel := context.WithCancel(ctx)
		events := make(chan officina.ModelEvent)
		var wg sync.WaitGroup
		wg.Go(func() {
			defer close(events)
			for _, e := range officinatest.TextReply("Hello").Events {
				select {
				case events <- e:
				case <-ctx.Done():
					return
				}
			}
		})
		defer func() {
			cancel()
			wg.Wait()
			close(m.released)
		}()
		for e := range events {
			if !yield(e, nil) {
				return
			}
		}
	}
}

func TestRun_EVT01_AConsumerThatBreaksMidRunLeavesNoGoroutine(t *testing.T) {
	t.Parallel()
	model := goroutineModel{released: make(chan struct{})}
	var c officina.Conversation

	_, result := stream(t.Context(), t, newAgent(t, model), &c, "Hi", officina.RunOptions{},
		func(officina.RunEvent) bool { return false })

	select {
	case <-model.released:
	default:
		t.Error("the model's stream was not released when the consumer broke")
	}
	if diff := cmp.Diff(officina.Result{Status: officina.Stopped, Stop: officina.Cancelled}, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if got := c.Messages(); len(got) != 0 {
		t.Errorf("conversation has %d messages, want none", len(got))
	}
	// The conversation is free for the next run.
	next := run(t, newAgent(t, officinatest.NewModel("scripted", officinatest.TextReply("Again."))), &c, "Hi",
		officina.RunOptions{})
	if next.Status != officina.Completed {
		t.Errorf("next run's status = %v, want Completed", next.Status)
	}
}

func TestRun_AGT08_AConsumerThatBreaksAtAnAppendKeepsAValidConversation(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	var c officina.Conversation

	_, result := stream(t.Context(), t, newAgent(t, model), &c, "Hi", officina.RunOptions{Context: "Monday."},
		func(e officina.RunEvent) bool {
			_, appended := e.(officina.ConversationAppended)
			return !appended
		})

	if diff := cmp.Diff(officina.Result{Status: officina.Completed, Text: "Hello."}, result); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	wantRoles := []officina.Role{officina.User, officina.Operator, officina.Assistant}
	if diff := cmp.Diff(wantRoles, roles(c.Messages())); diff != "" {
		t.Errorf("roles mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_AGT04_HundredConcurrentRunsOfOneAgentEachKeepTheirConversation(t *testing.T) {
	t.Parallel()
	const runs = 100
	replies := make([]officinatest.Reply, 2*runs)
	for i := range replies {
		replies[i] = officinatest.TextReply("ok")
	}
	model := officinatest.NewModel("scripted", replies...)
	agent := newAgent(t, model, tool("search", "Searches."))
	conversations := make([]officina.Conversation, runs)

	var wg sync.WaitGroup
	for i := range conversations {
		wg.Go(func() {
			for turn := range 2 {
				result, err := agent.Run(t.Context(), &conversations[i], fmt.Sprintf("run %d turn %d", i, turn),
					officina.RunOptions{})
				if err != nil || result.Status != officina.Completed {
					t.Errorf("run %d turn %d = %+v, %v; want Completed", i, turn, result, err)
				}
			}
		})
	}
	wg.Wait()

	for i := range conversations {
		var got []string
		for _, m := range conversations[i].Messages() {
			got = append(got, m.Text())
		}
		want := []string{fmt.Sprintf("run %d turn 0", i), "ok", fmt.Sprintf("run %d turn 1", i), "ok"}
		if diff := cmp.Diff(want, got); diff != "" {
			t.Errorf("conversation %d mismatch (-want +got):\n%s", i, diff)
		}
	}
	if got := len(model.Requests()); got != 2*runs {
		t.Errorf("model got %d requests, want %d", got, 2*runs)
	}
}

func TestRun_AGT04_ASecondRunOnAConversationInUseFails(t *testing.T) {
	t.Parallel()
	agent := newAgent(t, officinatest.NewModel("scripted", officinatest.TextReply("One."), officinatest.TextReply("Two.")))
	var c officina.Conversation

	var inner error
	_, result := stream(t.Context(), t, agent, &c, "Hi", officina.RunOptions{}, func(e officina.RunEvent) bool {
		if _, ok := e.(officina.TextStreamed); ok {
			_, inner = agent.Run(t.Context(), &c, "Again", officina.RunOptions{})
		}
		return true
	})

	if !errors.Is(inner, officina.ErrConversationInUse) {
		t.Errorf("second run error = %v, want %v", inner, officina.ErrConversationInUse)
	}
	if result.Text != "One." {
		t.Errorf("first run's text = %q, want %q", result.Text, "One.")
	}
}

func TestRun_BlankMessageOrRunContextIsAnError(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name    string
		message string
		opts    officina.RunOptions
		want    string
	}{
		{"blank message", " \n", officina.RunOptions{}, "run: blank message"},
		{"blank run context", "Hi", officina.RunOptions{Context: "\t"}, "run: blank run context"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			model := officinatest.NewModel("scripted", officinatest.TextReply("Unused."))

			_, err := newAgent(t, model).Run(t.Context(), nil, tt.message, tt.opts)

			if err == nil || err.Error() != tt.want {
				t.Errorf("Run() error = %v, want %q", err, tt.want)
			}
			if got := len(model.Requests()); got != 0 {
				t.Errorf("model got %d requests, want none", got)
			}
		})
	}
}

func TestRun_EVT01_EventsRangeOnceAndTheResultWaitsForThem(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	events, result := newAgent(t, model).Stream(t.Context(), nil, "Hi", officina.RunOptions{})

	var first, second int
	for range events {
		first++
	}
	for range events {
		second++
	}
	got, err := result()
	if err != nil {
		t.Fatalf("result() error = %v", err)
	}

	if first != 3 || second != 0 {
		t.Errorf("ranges reported %d and %d events, want 3 and 0", first, second)
	}
	if got.Text != "Hello." || len(model.Requests()) != 1 {
		t.Errorf("result text = %q after %d requests, want %q after 1", got.Text, len(model.Requests()), "Hello.")
	}
}

func TestNewAgent_AGT01_RejectsAnInvalidDefinition(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted")
	tests := []struct {
		name         string
		model        officina.Model
		instructions string
		tools        []officina.Tool
		want         string
	}{
		{"no model", nil, instructions, nil, "new agent: no model"},
		{"blank instructions", model, " ", nil, "new agent: blank instructions"},
		{"unnamed tool", model, instructions, []officina.Tool{tool("", "")}, "new agent: tool 1 has no name"},
		{
			"duplicate tool", model, instructions, []officina.Tool{tool("a", "A."), tool("b", ""), tool("a", "Other.")},
			`new agent: two tools are named "a"`,
		},
		{
			"schema not an object", model, instructions,
			[]officina.Tool{{Name: "a", InputSchema: jsontext.Value(`"string"`)}},
			`new agent: tool "a": input schema is not a JSON object`,
		},
		{
			"invalid schema", model, instructions, []officina.Tool{{Name: "a", InputSchema: jsontext.Value(`{"type":`)}},
			`new agent: tool "a": input schema is not a JSON object`,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			agent, err := officina.NewAgent(tt.model, tt.instructions, officina.AgentOptions{Tools: tt.tools})

			if err == nil || err.Error() != tt.want || agent != nil {
				t.Errorf("NewAgent() = %v, %v; want nil, %q", agent, err, tt.want)
			}
		})
	}
}

func TestNewAgent_AGT01_TheDefinitionDoesNotChangeWithTheCallersTools(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hi."))
	tools := []officina.Tool{tool("search", "Searches.")}
	agent := newAgent(t, model, tools...)

	tools[0].Description = "Changed."
	copy(tools[0].InputSchema, `{"type":"string"}`)
	run(t, agent, nil, "Hi", officina.RunOptions{})

	if diff := cmp.Diff([]officina.Tool{tool("search", "Searches.")}, model.Requests()[0].Tools); diff != "" {
		t.Errorf("tools mismatch (-want +got):\n%s", diff)
	}
}
