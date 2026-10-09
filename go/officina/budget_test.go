package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"fmt"
	"iter"
	"math"
	"slices"
	"testing"
	"testing/synctest"
	"time"

	"github.com/google/go-cmp/cmp"
	"github.com/google/go-cmp/cmp/cmpopts"
	"pgregory.net/rapid"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// opus is Opus 5.5's list price: $4 in, $20 out, $0.20 cache read; writes 1.25 times input for five minutes and
// twice input for an hour.
var opus = officina.Price{Input: 4, Output: 20, CacheRead: 0.20, CacheWrite: 5, CacheWriteHour: 8} //nolint:gochecknoglobals // A constant of a struct type.

// priced returns a scripted model with Opus 5.5's price.
func priced(replies ...officinatest.Reply) pricedModel {
	return pricedModel{officinatest.NewModel("scripted", replies...)}
}

// callTool returns a reply that calls the search tool, reporting usage.
func callTool(id string, usage officina.Usage) officinatest.Reply {
	return officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officinatest.ToolUseBlock(id, "search", `{}`)},
		officina.UsageReceived{Usage: usage}, officina.Finished{Reason: officina.FinishToolUse},
	}}
}

// cut returns a reply that the output limit cuts short, reporting usage.
func cut(usage officina.Usage) officinatest.Reply {
	return officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officinatest.TextBlock("Long")}, officina.UsageReceived{Usage: usage},
		officina.Finished{Reason: officina.FinishMaxTokens},
	}}
}

// ignoreDuration compares results without their duration.
func ignoreDuration() cmp.Option {
	return cmpopts.IgnoreFields(officina.Result{}, "Duration")
}

// costs returns limits of cost dollars.
func costs(cost float64) officina.Limits {
	return officina.Limits{Cost: &cost}
}

func TestRun_BUD02_BUD03_AResultReportsTokensCostCallsAndDurationAndEachUsageEventItsCost(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		slow := tool("search", "Searches.")
		slow.Handler = func(context.Context, jsontext.Value) (string, error) {
			time.Sleep(2 * time.Second)
			return "ok", nil
		}
		model := priced(
			officinatest.Reply{Events: []officina.ModelEvent{
				officina.BlockReceived{Block: officinatest.ToolUseBlock("c1", "search", `{}`)},
				officina.UsageReceived{Usage: officina.Usage{Input: 100, Output: 20, CacheWrite: 1_000, CacheWriteHour: 1_000}},
				officina.BlockReceived{Block: officinatest.ToolUseBlock("c2", "search", `{}`)},
				officina.UsageReceived{Usage: officina.Usage{Output: 5}},
				officina.Finished{Reason: officina.FinishToolUse},
			}},
			officinatest.Reply{Events: []officina.ModelEvent{
				officina.BlockReceived{Block: officinatest.TextBlock("Done.")},
				officina.UsageReceived{Usage: officina.Usage{Input: 10, Output: 5, CacheRead: 1_100}},
				officina.Finished{Reason: officina.FinishEnd},
			}})

		events, result := stream(t.Context(), t, newAgent(t, model, slow), nil, "Go.", officina.RunOptions{},
			func(officina.RunEvent) bool { return true })

		// $4 × 110 + $20 × 30 + $0.20 × 1,100 + $8 × 1,000 per million; the two reads overlap.
		want := officina.Result{
			Status: officina.Completed, Text: "Done.",
			Usage:      officina.Usage{Input: 110, Output: 30, CacheRead: 1_100, CacheWrite: 1_000, CacheWriteHour: 1_000},
			Cost:       0.00044 + 0.0006 + 0.00022 + 0.008,
			ModelCalls: 2, ToolCalls: 2, Duration: 2 * time.Second,
		}
		if diff := cmp.Diff(want, result, approx()); diff != "" {
			t.Errorf("result mismatch (-want +got):\n%s", diff)
		}
		var reported float64
		for _, e := range events {
			if u, ok := e.(officina.UsageReported); ok {
				reported += u.Cost
			}
		}
		if math.Abs(reported-result.Cost) > 1e-12 {
			t.Errorf("the usage events cost $%v in all, want the result's $%v", reported, result.Cost)
		}
	})
}

func TestRun_BUD01_ACostBudgetUsedUpStopsTheRunBeforeTheNextCallWithItsReason(t *testing.T) {
	t.Parallel()
	model := priced(callTool("c1", officina.Usage{Input: 100, Output: 50}), officinatest.TextReply("Unused."))
	var c officina.Conversation

	result := run(t, newAgent(t, model, tool("search", "Searches.")), &c, "Go.",
		officina.RunOptions{Budget: costs(0.001)})

	want := officina.Result{Status: officina.Stopped, Stop: officina.Budget,
		Detail: "the cost budget is used up: $0.0014 of $0.001", Usage: officina.Usage{Input: 100, Output: 50},
		Cost: 0.0014, ModelCalls: 1, ToolCalls: 1}
	if diff := cmp.Diff(want, result, approx(), ignoreDuration()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	if n := len(model.Requests()); n != 1 {
		t.Errorf("the model got %d requests, want 1", n)
	}
	if err := officinatest.CheckConversation(c.Messages()); err != nil || len(c.Messages()) != 3 {
		t.Errorf("the conversation has %d messages (%v), want the user's, the reply and its results", len(c.Messages()), err)
	}
}

func TestRun_BUD01_EachCallsOutputLimitIsLoweredToWhatTheRemainingCostAndTokensAllow(t *testing.T) {
	t.Parallel()
	model := priced(callTool("c1", officina.Usage{Input: 200, Output: 100}), officinatest.TextReply("Done."),
		officinatest.TextReply("Free."))
	agent := newAgent(t, model, tool("search", "Searches."))
	tokens := int64(1_000)
	budget := costs(0.01)
	budget.Tokens = &tokens

	run(t, agent, nil, "Go.", officina.RunOptions{Budget: budget})
	run(t, agent, nil, "Go.", officina.RunOptions{})

	// $0.01 buys 500 output tokens, and 1,000 tokens are left; then $0.0072 buys 360, and 700 tokens are left; then
	// nothing limits them.
	var got []int64
	for _, req := range model.Requests() {
		got = append(got, req.MaxOutputTokens)
	}
	if diff := cmp.Diff([]int64{500, 360, 0}, got); diff != "" {
		t.Errorf("output limits mismatch (-want +got):\n%s", diff)
	}
}

func TestRun_BUD01_ATokenBudgetAloneLowersTheOutputLimitToTheTokensLeft(t *testing.T) {
	t.Parallel()
	model := priced(callTool("c1", officina.Usage{Input: 200, Output: 100}), officinatest.TextReply("Done."))
	tokens := int64(1_000)

	run(t, newAgent(t, model, tool("search", "Searches.")), nil, "Go.",
		officina.RunOptions{Budget: officina.Limits{Tokens: &tokens}})

	if got := limits(model.Requests()); !slices.Equal(got, []int64{1_000, 700}) {
		t.Errorf("output limits = %v, want 1,000 then 700", got)
	}
}

func TestRun_BUD01_TheCostBudgetAtItsBoundaries(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name       string
		outputFree bool
		cost       float64
		requests   []int64
	}{
		// $0.00002 buys exactly one output token at $20 per million: the call is made, limited to it.
		{"what is left buys one output token", false, 0.00002, []int64{1}},
		// Nothing is left, so nothing more may be spent, even where output costs nothing.
		{"nothing left, output free", true, 0, nil},
		// Output that costs nothing is not limited by what is left.
		{"something left, output free", true, 0.01, []int64{0}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			scripted := officinatest.NewModel("scripted", officinatest.TextReply("Y"))
			var model officina.Model = pricedModel{scripted}
			if tt.outputFree {
				model = inputPriced{scripted}
			}

			result := run(t, newAgent(t, model), nil, "Go.", officina.RunOptions{Budget: costs(tt.cost)})

			if got := limits(scripted.Requests()); !slices.Equal(got, tt.requests) {
				t.Errorf("output limits = %v, want %v", got, tt.requests)
			}
			if wantStop := tt.requests == nil; wantStop != (result.Stop == officina.Budget) {
				t.Errorf("result = %+v, stopped for the budget: want %v", result, wantStop)
			}
		})
	}
}

// inputPriced is a scripted model whose output costs nothing.
type inputPriced struct {
	*officinatest.Model
}

func (inputPriced) Info() officina.ModelInfo {
	return officina.ModelInfo{Price: officina.Price{Input: 4}}
}

// limits returns the output limit of each request.
func limits(requests []officina.Request) []int64 {
	var got []int64
	for _, req := range requests {
		got = append(got, req.MaxOutputTokens)
	}
	return got
}

func TestRun_BUD01_AReplyCutShortByTheLoweredLimitStopsForTheBudgetAndOneCutByTheModelsOwnDoesNot(t *testing.T) {
	t.Parallel()
	model := priced(cut(officina.Usage{Input: 10, Output: 50}), cut(officina.Usage{Input: 10, Output: 30}))
	agent := newAgent(t, model)

	budgetCut := run(t, agent, nil, "Go.", officina.RunOptions{Budget: costs(0.001)})
	ownCut := run(t, agent, nil, "Go.", officina.RunOptions{Budget: costs(0.01)})

	if budgetCut.Stop != officina.Budget || budgetCut.Detail != "the cost budget is used up: $0.00104 of $0.001" {
		t.Errorf("cut by the budget = %+v, want stopped for the budget", budgetCut)
	}
	if ownCut.Stop != officina.OutputLimit {
		t.Errorf("cut by the model's own limit = %+v, want stopped for the output limit", ownCut)
	}
}

func TestRun_BUD01_ModelCallTimeAndTokenLimitsStopTheRunBeforeTheNextCall(t *testing.T) {
	t.Parallel()
	calls, tokens := 1, int64(100)
	tests := []struct {
		name   string
		budget officina.Limits
		want   string
	}{
		{"model calls", officina.Limits{ModelCalls: &calls}, "the model call budget is used up: 1 of 1"},
		{"tokens", officina.Limits{Tokens: &tokens}, "the token budget is used up: 1,500 of 100 tokens"},
		{"time", officina.Limits{Time: new(5 * time.Second)}, "the time budget is used up: 10 s of 5 s"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			synctest.Test(t, func(t *testing.T) {
				slow := tool("search", "Searches.")
				slow.Handler = func(context.Context, jsontext.Value) (string, error) {
					time.Sleep(10 * time.Second)
					return "ok", nil
				}
				model := priced(callTool("c1", officina.Usage{Input: 1_000, Output: 500}), officinatest.TextReply("Unused."))

				result := run(t, newAgent(t, model, slow), nil, "Go.", officina.RunOptions{Budget: tt.budget})

				if result.Stop != officina.Budget || result.Detail != tt.want {
					t.Errorf("result = %+v, want stopped: %s", result, tt.want)
				}
				if n := len(model.Requests()); n != 1 {
					t.Errorf("the model got %d requests, want 1", n)
				}
			})
		})
	}
}

func TestRun_BUD01_AUsedUpBudgetStopsBeforeTheFirstCallAndAppendsNothing(t *testing.T) {
	t.Parallel()
	model := priced(officinatest.TextReply("Unused."))
	var c officina.Conversation

	result := run(t, newAgent(t, model), &c, "Go.", officina.RunOptions{Budget: costs(0)})

	if result.Stop != officina.Budget || result.Detail != "the cost budget is used up: $0 of $0" {
		t.Errorf("result = %+v, want stopped for the budget", result)
	}
	if len(model.Requests()) != 0 || len(c.Messages()) != 0 {
		t.Errorf("the model got %d requests and the conversation has %d messages, want none",
			len(model.Requests()), len(c.Messages()))
	}
}

func TestRun_BUD01_ACostBudgetIsUsedUpWhenWhatIsLeftBuysLessThanOneOutputToken(t *testing.T) {
	t.Parallel()
	// The first call costs $0.0014; the $0.00001 left would buy half an output token at $20 per million.
	model := priced(callTool("c1", officina.Usage{Input: 100, Output: 50}), officinatest.TextReply("Unused."))

	result := run(t, newAgent(t, model, tool("search", "Searches.")), nil, "Go.",
		officina.RunOptions{Budget: costs(0.00141)})

	if result.Stop != officina.Budget || result.Detail != "the cost budget is used up: $0.0014 of $0.00141" {
		t.Errorf("result = %+v, want stopped for the budget", result)
	}
}

func TestRun_BUD01_ACostBudgetNeedsAModelWithAPrice(t *testing.T) {
	t.Parallel()
	agent := newAgent(t, officinatest.NewModel("scripted"))

	if _, err := agent.Run(t.Context(), nil, "Go.", officina.RunOptions{Budget: costs(1)}); err == nil {
		t.Error("Run() error = nil for a cost budget on a model without a price")
	}
}

func TestRun_BUD01_ACallBudgetUsedUpOnTheLastAllowedCallStopsForTheBudgetNotTheIterationLimit(t *testing.T) {
	t.Parallel()
	const maxModelCalls = 25
	var replies []officinatest.Reply
	for i := range maxModelCalls - 1 {
		replies = append(replies, callTool(fmt.Sprintf("c%d", i), officina.Usage{Input: 1, Output: 1}))
	}
	calls := maxModelCalls - 1

	result := run(t, newAgent(t, priced(replies...), tool("search", "Searches.")), nil, "Go.",
		officina.RunOptions{Budget: officina.Limits{ModelCalls: &calls}})

	if result.Stop != officina.Budget || result.Detail != "the model call budget is used up: 24 of 24" {
		t.Errorf("result = %+v, want stopped for the budget", result)
	}
}

func TestRun_BUD01_AReplyCutByTheModelsOwnLimitStopsForThatLimitEvenWhenACallBudgetIsUsedUp(t *testing.T) {
	t.Parallel()
	calls := 1

	result := run(t, newAgent(t, priced(cut(officina.Usage{Input: 10, Output: 10}))), nil, "Go.",
		officina.RunOptions{Budget: officina.Limits{ModelCalls: &calls}})

	if result.Stop != officina.OutputLimit {
		t.Errorf("result = %+v, want stopped for the output limit", result)
	}
}

func TestRun_BUD01_ATimeBudgetIsUsedUpTheMomentItIsReached(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		slow := tool("search", "Searches.")
		slow.Handler = func(context.Context, jsontext.Value) (string, error) {
			time.Sleep(2 * time.Second)
			return "ok", nil
		}
		model := priced(callTool("c1", officina.Usage{Input: 1, Output: 1}), officinatest.TextReply("Unused."))

		result := run(t, newAgent(t, model, slow), nil, "Go.",
			officina.RunOptions{Budget: officina.Limits{Time: new(2 * time.Second)}})

		if result.Stop != officina.Budget || result.Detail != "the time budget is used up: 2 s of 2 s" {
			t.Errorf("result = %+v, want stopped for the budget", result)
		}
	})
}

// plannedCall is one model call of the overshoot property: the prompt's tokens, the output it wants, and whether
// it compacts, which reads the prompt twice.
type plannedCall struct {
	Input, CacheRead, CacheWrite, Output int64
	Compacts                             bool
}

// plannedModel makes the planned calls in order, each but the last asking for a tool, and keeps each reply within
// the request's output limit, as a real model does.
type plannedModel struct {
	plan []plannedCall
	made int
}

func (*plannedModel) Settings() string { return "planned" }

func (*plannedModel) Info() officina.ModelInfo { return officina.ModelInfo{Price: opus} }

// Stream makes the next planned call, within the request's output limit.
func (m *plannedModel) Stream(_ context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error] {
	call := m.plan[m.made]
	m.made++
	factor := int64(1)
	if call.Compacts {
		factor = 2
	}
	output := call.Output
	if req.MaxOutputTokens > 0 {
		output = min(output, req.MaxOutputTokens)
	}
	block := officinatest.ToolUseBlock(fmt.Sprintf("c%d", m.made), "search", `{}`)
	reason := officina.FinishToolUse
	if m.made == len(m.plan) {
		block, reason = officinatest.TextBlock("Done."), officina.FinishEnd
	}
	events := []officina.ModelEvent{
		officina.BlockReceived{Block: block},
		officina.UsageReceived{Usage: officina.Usage{Input: call.Input * factor, Output: output,
			CacheRead: call.CacheRead * factor, CacheWrite: call.CacheWrite * factor}},
		officina.Finished{Reason: reason},
	}
	return func(yield func(officina.ModelEvent, error) bool) {
		for _, e := range events {
			if !yield(e, nil) {
				return
			}
		}
	}
}

func TestRun_TEST07_ABudgetIsOvershotByAtMostOneCallsInputOrTwiceThatWhenItCompacts(t *testing.T) {
	t.Parallel()
	rapid.Check(t, func(t *rapid.T) {
		var budget officina.Limits
		if rapid.Bool().Draw(t, "cost limited") {
			cost := float64(rapid.IntRange(1, 500).Draw(t, "cents")) / 10_000
			budget.Cost = &cost
		}
		if rapid.Bool().Draw(t, "tokens limited") {
			budget.Tokens = new(rapid.Int64Range(1, 60_000).Draw(t, "tokens"))
		}
		if rapid.Bool().Draw(t, "calls limited") {
			budget.ModelCalls = new(rapid.IntRange(1, 8).Draw(t, "calls"))
		}
		plan := rapid.SliceOfN(rapid.Custom(func(t *rapid.T) plannedCall {
			return plannedCall{
				Input:      rapid.Int64Range(1, 3_000).Draw(t, "input"),
				CacheRead:  rapid.Int64Range(0, 20_000).Draw(t, "cache read"),
				CacheWrite: rapid.Int64Range(0, 5_000).Draw(t, "cache write"),
				Output:     rapid.Int64Range(0, 8_000).Draw(t, "output"),
				Compacts:   rapid.Bool().Draw(t, "compacts"),
			}
		}), 1, 10).Draw(t, "plan")
		model := &plannedModel{plan: plan}
		agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{Tools: []officina.Tool{tool("search", "Searches.")}})
		if err != nil {
			t.Fatalf("NewAgent() error = %v", err)
		}

		result, err := agent.Run(context.Background(), nil, "Go.", officina.RunOptions{Budget: budget})
		if err != nil {
			t.Fatalf("Run() error = %v", err)
		}

		if result.Status != officina.Completed && result.Stop != officina.Budget {
			t.Fatalf("result = %+v, want completed or stopped for the budget", result)
		}
		// Only the last call can cross a limit, as each starts below all of them; it overshoots by its input at most.
		var last plannedCall
		if model.made > 0 {
			last = plan[model.made-1]
		}
		factor := int64(1)
		if last.Compacts {
			factor = 2
		}
		input := (last.Input + last.CacheRead + last.CacheWrite) * factor
		inputCost := float64(factor) * (float64(last.Input)*opus.Input + float64(last.CacheRead)*opus.CacheRead +
			float64(last.CacheWrite)*opus.CacheWrite) / 1e6
		used := result.Usage.Input + result.Usage.Output + result.Usage.CacheRead + result.Usage.CacheWrite
		if budget.Tokens != nil && used > *budget.Tokens+input {
			t.Errorf("%d tokens used of %d, more than the last call's input (%d) over", used, *budget.Tokens, input)
		}
		// Costs add up in floating point: a billionth of a cent over is rounding, not overshoot.
		if budget.Cost != nil && result.Cost > *budget.Cost+inputCost+1e-11 {
			t.Errorf("$%v spent of $%v, more than the last call's input ($%v) over", result.Cost, *budget.Cost, inputCost)
		}
		if budget.ModelCalls != nil && result.ModelCalls > *budget.ModelCalls {
			t.Errorf("%d model calls of %d", result.ModelCalls, *budget.ModelCalls)
		}
		if result.ModelCalls != model.made {
			t.Errorf("the result counts %d model calls, want the %d made", result.ModelCalls, model.made)
		}
	})
}
