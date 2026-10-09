package officina_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"slices"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestRun_CTX04_AChangedPrefixFailsWithPrefixMismatchBeforeAnyModelCall(t *testing.T) {
	t.Parallel()
	search := tool("search", "Searches the catalogue.")
	tests := []struct {
		name         string
		settings     string
		instructions string
		tools        []officina.Tool
	}{
		{"instructions", "scripted", "You are a terse assistant.", []officina.Tool{search}},
		{"tool description", "scripted", instructions, []officina.Tool{tool("search", "Finds books.")}},
		{
			"tool schema", "scripted", instructions,
			[]officina.Tool{{Name: "search", Description: search.Description, InputSchema: jsontext.Value(`{"type":"object","properties":{}}`)}},
		},
		{"added tool", "scripted", instructions, []officina.Tool{search, tool("order", "Places an order.")}},
		{"removed tool", "scripted", instructions, nil},
		{"model settings", "scripted, effort high", instructions, []officina.Tool{search}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			var c officina.Conversation
			run(t, newAgent(t, officinatest.NewModel("scripted", officinatest.TextReply("Hi.")), search), &c, "Hi",
				officina.RunOptions{})
			saved := c.Messages()
			model := officinatest.NewModel(tt.settings, officinatest.TextReply("Unused."))
			changed, err := officina.NewAgent(model, tt.instructions, officina.AgentOptions{Tools: tt.tools})
			if err != nil {
				t.Fatalf("NewAgent() error = %v", err)
			}

			result := run(t, changed, &c, "Again", officina.RunOptions{})

			if result.Status != officina.Failed || result.Failure != officina.PrefixMismatch {
				t.Errorf("result = %+v, want Failed with PrefixMismatch", result)
			}
			if got := len(model.Requests()); got != 0 {
				t.Errorf("model got %d requests, want none", got)
			}
			if diff := cmp.Diff(saved, c.Messages()); diff != "" {
				t.Errorf("conversation changed (-want +got):\n%s", diff)
			}
		})
	}
}

func TestRun_CTX01_ToolsGivenInAnotherOrderAreTheSamePrefix(t *testing.T) {
	t.Parallel()
	tools := []officina.Tool{tool("search", "Searches."), tool("order", "Orders.")}
	model := officinatest.NewModel("scripted", officinatest.TextReply("One."), officinatest.TextReply("Two."))
	var c officina.Conversation

	run(t, newAgent(t, model, tools...), &c, "Hi", officina.RunOptions{})
	slices.Reverse(tools)
	result := run(t, newAgent(t, model, tools...), &c, "Again", officina.RunOptions{})

	if result.Status != officina.Completed {
		t.Errorf("result = %+v, want Completed", result)
	}
	for i, req := range model.Requests() {
		var names []string
		for _, tool := range req.Tools {
			names = append(names, tool.Name)
		}
		if diff := cmp.Diff([]string{"order", "search"}, names); diff != "" {
			t.Errorf("request %d tools mismatch (-want +got):\n%s", i+1, diff)
		}
	}
}

func TestRun_TEST02_ThePrefixIsStableAcrossTurnsAndAcrossSaveRestartAndResume(t *testing.T) {
	t.Parallel()
	// Before the restart: two turns, with run context, and a reasoning block kept as raw JSON.
	thinking := officina.Block{Raw: jsontext.Value(`{"type":"thinking","thinking":"caf\u00e9 \u003cb\u003e","signature":"c2ln"}`)}
	before := officinatest.NewModel("scripted",
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.BlockReceived{Block: thinking},
			officina.BlockReceived{Block: officinatest.TextBlock("Hello <you> & «friend».")},
			officina.Finished{Reason: officina.FinishEnd},
		}},
		officinatest.TextReply("Paris."))
	agent := newAgent(t, before, tool("search", "Searches."), tool("order", "Orders."))
	var c officina.Conversation
	run(t, agent, &c, "Hi", officina.RunOptions{Context: "Date: 2026-10-05."})
	run(t, agent, &c, "Capital of France?", officina.RunOptions{})
	saved, err := json.Marshal(&c)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}

	// After it: only the JSON is left, and the agent is built afresh, its tools given in another order.
	after := officinatest.NewModel("scripted", officinatest.TextReply("Berlin."), officinatest.TextReply("Rome."))
	var resumed officina.Conversation
	if err := json.Unmarshal(saved, &resumed); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	rebuilt := newAgent(t, after, tool("order", "Orders."), tool("search", "Searches."))
	run(t, rebuilt, &resumed, "And Germany?", officina.RunOptions{Context: "Date: 2026-10-06."})
	last := run(t, rebuilt, &resumed, "And Italy?", officina.RunOptions{})

	if last.Text != "Rome." {
		t.Errorf("last text = %q, want %q", last.Text, "Rome.")
	}
	requests := append(before.Requests(), after.Requests()...)
	if len(requests) != 4 {
		t.Fatalf("got %d requests, want 4", len(requests))
	}
	if err := officinatest.CheckPrefix(requests); err != nil {
		t.Errorf("CheckPrefix() = %v, want nil", err)
	}
}
