package chat_test

import (
	"maps"
	"slices"
	"strings"
	"testing"
	"testing/synctest"
	"time"

	"github.com/google/go-cmp/cmp"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/examples/chat"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

// The chat assistant sample, offline, with only the model, the clock (testing/synctest's) and storage replaced.

// assistant returns an assistant of model with memory, keeping conversations in conversations.
func assistant(t *testing.T, model officina.Model, memory officina.MemoryStore,
	conversations map[string][]byte,
) *chat.Assistant {
	t.Helper()
	agent, err := chat.NewAgent(model, memory)
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return chat.NewAssistant(agent, conversations)
}

// reply answers message from user and returns the reply's text, failing t unless the run completed.
func reply(t *testing.T, a *chat.Assistant, user, message string) string {
	t.Helper()
	res, err := a.Reply(t.Context(), user, message)
	if err != nil || res.Status != officina.Completed {
		t.Fatalf("Reply(%q, %q) = %v %q, %v; want it completed", user, message, res.Status, res.Detail, err)
	}
	return res.Text
}

// lastResult returns the first tool result of the last request model received.
func lastResult(model *officinatest.Model) officina.ToolResult {
	requests := model.Requests()
	messages := requests[len(requests)-1].Messages
	return *messages[len(messages)-1].Blocks[0].ToolResult
}

func TestReply_GEN06_TEST02_AUsersConversationGoesOnAfterARestartWithTheSamePrefixAndItsToolAnswers(t *testing.T) {
	t.Parallel()
	synctest.Test(t, func(t *testing.T) {
		conversations := map[string][]byte{}
		memory := &officina.MapMemoryStore{}
		before := officinatest.NewModel("scripted",
			officinatest.ToolUseReply(officinatest.ToolUseBlock("t1", "celsius_to_fahrenheit", `{"celsius":20}`)),
			officinatest.TextReply("20 °C is 68 °F."))
		first := reply(t, assistant(t, before, memory, conversations), "ana", "What is 20 °C in Fahrenheit?")

		// A restart, a day later: only the stored JSON is left, and the agent is built afresh.
		time.Sleep(24 * time.Hour)
		after := officinatest.NewModel("scripted",
			officinatest.ToolUseReply(officinatest.ToolUseBlock("t2", "celsius_to_fahrenheit", `{"celsius":30}`)),
			officinatest.TextReply("30 °C is 86 °F."))
		second := reply(t, assistant(t, after, memory, conversations), "ana", "And 30?")

		if first != "20 °C is 68 °F." || second != "30 °C is 86 °F." {
			t.Errorf("replies = %q, %q", first, second)
		}
		if diff := cmp.Diff(officina.ToolResult{CallID: "t2", Content: "86"}, lastResult(after)); diff != "" {
			t.Errorf("the conversion's result mismatch (-want +got):\n%s", diff)
		}
		if err := officinatest.CheckPrefix(append(before.Requests(), after.Requests()...)); err != nil {
			t.Errorf("CheckPrefix() = %v", err)
		}
		// The date came as run context each day, after the user's message.
		var dates []string
		for _, m := range after.Requests()[1].Messages {
			if m.Role == officina.Operator {
				dates = append(dates, m.Text())
			}
		}
		if diff := cmp.Diff([]string{"Today is 2000-01-01.", "Today is 2000-01-02."}, dates); diff != "" {
			t.Errorf("run context mismatch (-want +got):\n%s", diff)
		}
		if users := slices.Sorted(maps.Keys(conversations)); !slices.Equal(users, []string{"ana"}) {
			t.Errorf("stored conversations of %q, want only ana's", users)
		}
	})
}

func TestReply_GEN06_AConversationOfAChangedAgentIsStartedAnewRatherThanFailing(t *testing.T) {
	t.Parallel()
	conversations := map[string][]byte{}
	memory := &officina.MapMemoryStore{}
	reply(t, assistant(t, officinatest.NewModel("scripted", officinatest.TextReply("Hello.")), memory, conversations),
		"ana", "Hi.")

	// An upgrade changed the model settings, which are part of the prefix.
	upgraded := officinatest.NewModel("scripted, effort high", officinatest.TextReply("Hello again."))
	got := reply(t, assistant(t, upgraded, memory, conversations), "ana", "Hi again.")

	if got != "Hello again." {
		t.Errorf("reply = %q, want Hello again.", got)
	}
	var sent []string
	for _, m := range upgraded.Requests()[0].Messages {
		sent = append(sent, m.Text())
	}
	if len(sent) != 2 || sent[0] != "Hi again." {
		t.Errorf("the upgraded agent's first request holds %q, want only the new message and its run context", sent)
	}
	if strings.Contains(string(conversations["ana"]), `"Hi."`) {
		t.Errorf("the stored conversation still holds the old one: %s", conversations["ana"])
	}
}

func TestReply_GEN06_MEM01_MemoryIsKeptPerUserAcrossConversationsAndOneUserNeverSeesAnothers(t *testing.T) {
	t.Parallel()
	conversations := map[string][]byte{}
	memory := &officina.MapMemoryStore{}
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("m1", "memory",
			`{"command":"create","path":"/memories/preferences.md","file_text":"Units: Fahrenheit.\n"}`)),
		officinatest.TextReply("Noted: Fahrenheit from now on."),
		officinatest.ToolUseReply(officinatest.ToolUseBlock("m2", "memory", `{"command":"view","path":"/memories"}`)),
		officinatest.TextReply("I don't remember anything about you yet."))
	a := assistant(t, model, memory, conversations)

	reply(t, a, "ana", "I prefer Fahrenheit.")
	reply(t, a, "ben", "What do you remember about me?")

	if got := lastResult(model).Content; strings.Contains(got, "preferences.md") {
		t.Errorf("Ben's view of his memory shows Ana's: %q", got)
	}

	// A new conversation for Ana, as when the host forgets it, still finds her preference in memory.
	clear(conversations)
	later := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("m3", "memory",
			`{"command":"view","path":"/memories/preferences.md"}`)),
		officinatest.TextReply("It's 68 °F."))
	reply(t, assistant(t, later, memory, conversations), "ana", "What's 20 °C?")

	if got := lastResult(later).Content; !strings.Contains(got, "Units: Fahrenheit.") {
		t.Errorf("Ana's view of her preferences = %q, want her preference", got)
	}
	files, err := memory.List(t.Context(), "ben")
	if err != nil || len(files) != 0 {
		t.Errorf("Ben's memory = %v, %v; want it empty", files, err)
	}
	// Memory is a tool the model calls, never part of the instructions.
	if strings.Contains(later.Requests()[0].Instructions, "Fahrenheit.") {
		t.Error("the instructions hold the memory")
	}
}
