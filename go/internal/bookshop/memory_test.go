package bookshop_test

import (
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// Memory per staff member, end to end. Each session is an application start; what one remembers, the next finds
// only in the memory store.

func TestConsole_APP11_MemoryShowsWhatIsRememberedForTheStaffMemberAtTheCounter(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	memory := &officina.MapMemoryStore{}
	for _, f := range []struct{ scope, path, text string }{
		{"sam", "preferences.md", "Prices with tax.\nBrief answers.\n"}, {"sam", "customers/ana.md", "Likes crime."},
		{"ana", "notes.md", "Ana's notes."},
	} {
		if err := memory.Write(t.Context(), f.scope, f.path, f.text); err != nil {
			t.Fatalf("Write() error = %v", err)
		}
	}
	cfg := bookshop.Config{Memory: memory}

	sam := sessionOf(t, cfg, d, officinatest.NewModel("scripted"), "", "../sam", "Sam", "/memory", "/quit")
	ben := sessionOf(t, cfg, d, officinatest.NewModel("scripted"), "", "Ben", "/memory", "/quit")

	inOrder(t, sam, "Your name: ../sam\nThat name cannot be used. Please give another name.\n", "Your name: Sam\n",
		"you> /memory\nRemembered:\n/memories/customers/ana.md\n  Likes crime.\n/memories/preferences.md\n"+
			"  Prices with tax.\n  Brief answers.\nyou> /quit")
	if strings.Contains(sam, "Ana's") {
		t.Errorf("Sam's /memory shows Ana's notes:\n%s", sam)
	}
	inOrder(t, ben, "you> /memory\nNothing remembered yet.\n")
}

func TestConsole_APP11_MEM03_MEM04_MEM05_APreferenceSavedInOneSessionIsAppliedInANewOne(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	dir := t.TempDir()
	cfg := bookshop.Config{Memory: officina.NewFileMemoryStore(dir)}
	first := officinatest.NewModel("scripted",
		sayThenCall("I'll remember that.", officinatest.ToolUseBlock("save-1", "memory",
			`{"command":"create","path":"/memories/preferences.md","file_text":"Show prices with tax.\n"}`)),
		officinatest.TextReply("Noted: prices with tax from now on."))
	sessionOf(t, cfg, d, first, "", "Sam", "I prefer prices with tax.", "/quit")

	// A new start, with a new store over the same files.
	cfg.Memory = officina.NewFileMemoryStore(dir)
	second := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("view-1", "memory",
			`{"command":"view","path":"/memories/preferences.md"}`)),
		officinatest.TextReply("The Winter Archive costs £7.54 with tax."))
	transcript := sessionOf(t, cfg, d, second, "", "sam", "What does book 144 cost?", "/memory", "/quit")

	if diff := cmp.Diff([]string{"Here's the content of /memories/preferences.md with line numbers:\n     1\tShow prices with tax."},
		contentsOf(results(t, second, -1))); diff != "" {
		t.Errorf("view result mismatch (-want +got):\n%s", diff)
	}
	inOrder(t, transcript, "  > memory ", "  < memory: ok", "costs £7.54 with tax.", "/memories/preferences.md\n",
		"  Show prices with tax.\n")
	// The run context names who is at the counter; memory never enters the instructions.
	if context := second.Requests()[0].Messages[1].Text(); !strings.Contains(context, "The staff member using the assistant is sam.") {
		t.Errorf("run context = %q, want it to name sam", context)
	}
	for _, r := range append(first.Requests(), second.Requests()...) {
		if r.Instructions != first.Requests()[0].Instructions || strings.Contains(r.Instructions, "Show prices") {
			t.Errorf("instructions changed with memory: %q", r.Instructions)
		}
	}
	// The write was audited before it ran, in Sam's scope.
	if scope := scalar[string](t, d, "select memory_scope from audit where call_id = 'save-1' and kind = 'ToolStarted'"); scope != "sam" {
		t.Errorf("the write's audited memory scope = %q, want sam", scope)
	}
	if !scalar[bool](t, d, `select (select id from audit where call_id = 'save-1' and kind = 'ToolStarted') <
		(select id from audit where call_id = 'save-1' and kind = 'ToolEnded')`) {
		t.Error("the write's outcome was audited before its attempt")
	}
}

// contentsOf returns the results' contents.
func contentsOf(results []officina.ToolResult) []string {
	var got []string
	for _, r := range results {
		got = append(got, r.Content)
	}
	return got
}
