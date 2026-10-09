package bookshop_test

import (
	"encoding/json/jsontext"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestConsole_APP17_HIST04_DemoModeCompactsAndClearsEarlyAndReportsEachOnceInTheConsoleAndTheAudit(t *testing.T) {
	t.Parallel()
	clearing := officina.ClearingReported{Tokens: 4_892, ToolCalls: 2}
	search := sayThenCall("Searching.", officinatest.ToolUseBlock("c1", "search_books", `{"title":"Winter"}`))
	search.Events = append(search.Events[:len(search.Events)-1], clearing, search.Events[len(search.Events)-1])
	model := officinatest.NewModel("scripted", search, officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officina.Block{Raw: jsontext.Value(`{"type":"compaction","content":"Summary."}`)}},
		// The provider clears the same results again on every later call.
		clearing,
		officina.CompactionReported{Tokens: 52_753, SummaryTokens: 578},
		officina.TextDelta{Text: "It is on shelf Q2."},
		officina.BlockReceived{Block: officinatest.TextBlock("It is on shelf Q2.")},
		officina.Finished{Reason: officina.FinishEnd},
	}})

	transcript := sessionOf(t, bookshop.Config{Demo: true}, newDatabase(t), model, "", "Sam",
		"Where is The Winter Archive?", "/audit", "/quit")

	inOrder(t, transcript,
		"Bookshop Assistant. Type /help for commands.\n",
		"Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 tool calls.\n",
		"  ~ Old tool results cleared: 2 tool calls, 4,892 tokens.\n",
		"  < search_books: ok\n",
		"  ~ Conversation compacted: 52,753 tokens summarized into 578.\n",
		"It is on shelf Q2.\n",
		"you> /audit\n",
		"Cleared", "Results of 2 tool calls cleared: 4,892 tokens.\n",
		"Cleared", "Results of 2 tool calls cleared: 4,892 tokens.\n",
		"Compacted", "52,753 tokens summarized into 578.\n",
		"you> /quit\n")
	if n := strings.Count(transcript, "~ Old tool results cleared"); n != 1 {
		t.Errorf("the clearing is shown %d times, want once:\n%s", n, transcript)
	}
	want := officina.ContextManagement{CompactAt: 50_000, ClearToolResults: officina.ToolResultClearing{After: 12, Keep: 10}}
	for i, r := range model.Requests() {
		if diff := cmp.Diff(want, r.ContextManagement); diff != "" {
			t.Errorf("request %d context management mismatch (-want +got):\n%s", i+1, diff)
		}
	}
}

func TestConsole_HIST01_HIST02_OutsideDemoModeCompactionAndClearingComeLate(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))

	transcript := session(t, newDatabase(t), model, "", "Sam", "Hi", "/quit")

	if strings.Contains(transcript, "Demo mode") {
		t.Errorf("the transcript announces demo mode:\n%s", transcript)
	}
	want := officina.ContextManagement{CompactAt: 150_000, ClearToolResults: officina.ToolResultClearing{
		After: 20, Keep: 5, AtLeastTokens: 20_000,
	}}
	if diff := cmp.Diff(want, model.Requests()[0].ContextManagement); diff != "" {
		t.Errorf("context management mismatch (-want +got):\n%s", diff)
	}
}
