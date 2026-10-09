package bookshop_test

import (
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// The session summarizer, end to end: the real console, agents, core and database, with the chat and summarizer
// models and the staff member scripted.

// summarized runs a console session as session does, with summaries as the summarizer's model.
func summarized(t *testing.T, d *database, model, summaries *officinatest.Model, script ...any) string {
	t.Helper()
	return sessionOf(t, bookshop.Config{Summarizer: priced{summaries}}, d, model, "", script...)
}

// summaryReply returns the summarizer's reply with a title, a summary and changes.
func summaryReply(t *testing.T, title, text string, changes ...string) officinatest.Reply {
	t.Helper()
	if changes == nil {
		changes = []string{}
	}
	data, err := json.Marshal(map[string]any{"title": title, "summary": text, "changes": changes})
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	return say(string(data), officina.Usage{Input: 1000, Output: 100})
}

// before returns transcript up to where it holds text.
func before(t *testing.T, transcript, text string) string {
	t.Helper()
	at := strings.Index(transcript, text)
	if at < 0 {
		t.Fatalf("transcript lacks %q:\n%s", text, transcript)
	}
	return transcript[:at]
}

func TestConsole_APP15_LeavingASessionSummarizesItAndSessionsShowsItsTitleSummaryAndChanges(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	model := officinatest.NewModel("scripted",
		sayThenCall("I'll add two copies.", officinatest.ToolUseBlock("c1", "restock_book", `{"bookId":320,"quantity":2}`)),
		officinatest.TextReply("Done: two more copies."))
	summaries := officinatest.NewModel("summarizer", summaryReply(t, "Restock of book 320",
		"Sam asked for two more copies of book 320.", "Book 320: 2 copies added"))

	first := summarized(t, d, model, summaries, "Sam", "Restock book 320 with 2.", "y", "/quit")
	id := sessionID(t, first)
	listing := summarized(t, d, officinatest.NewModel("scripted"), officinatest.NewModel("summarizer"), "Sam",
		"/sessions", "/quit")

	inOrder(t, first, "Done: two more copies.", "you> /quit\n", "Session "+id+" summarized: Restock of book 320\n")
	inOrder(t, listing, "  "+id+"  ", "  Sam  Restock of book 320  $",
		"\n    Sam asked for two more copies of book 320.\n", "    Changes: Book 320: 2 copies added\n")
	// The summarizer reads the transcript as plain text in one user message, with no tools and the output schema.
	requests := summaries.Requests()
	if len(requests) != 1 {
		t.Fatalf("the summarizer made %d requests, want 1", len(requests))
	}
	req := requests[0]
	if len(req.Tools) != 0 || len(req.Messages) != 1 || req.Messages[0].Role != officina.User || req.OutputSchema == nil {
		t.Errorf("summarizer request = %d tools, %d messages, output schema %s; want none, one user message, one",
			len(req.Tools), len(req.Messages), req.OutputSchema)
	}
	text := req.Messages[0].Text()
	inOrder(t, text, "Staff: Restock book 320 with 2.\n", "Assistant: I'll add two copies.\n",
		`Tool call restock_book {"bookId":320,"quantity":2}`+"\n", "Tool result of restock_book: ",
		"Assistant: Done: two more copies.\n")
	if strings.Contains(text, "Today is") {
		t.Errorf("the transcript holds the run context:\n%s", text)
	}
	// The summary's cost, $0.006 at Opus 5.5's price, is the session's too.
	if cost := scalar[float64](t, d, "select cost::float8 from sessions where id = $1", id); cost < 0.006 {
		t.Errorf("the session's cost is $%.4f, without the summary's", cost)
	}
}

func TestConsole_APP15_NewResumeAndQuitEachSummarizeTheSessionLeftAndAnUnchangedSessionIsNotSummarizedAgain(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	summaries := officinatest.NewModel("summarizer", summaryReply(t, "Greeting", "Sam said hello."),
		summaryReply(t, "Second greeting", "Sam said hello again."),
		summaryReply(t, "Third greeting", "Sam said hello a third time."))

	first := summarized(t, d, officinatest.NewModel("scripted", officinatest.TextReply("Hello."),
		officinatest.TextReply("Hello again.")), summaries, "Sam", "Hi.", "/new", "Hi again.", "/quit")
	one, two := sessionID(t, before(t, first, "you> /new")), sessionID(t, first)
	second := summarized(t, d, officinatest.NewModel("scripted", officinatest.TextReply("Hello a third time.")),
		summaries, "Sam", "Hi a third time.", "/resume "+one, "/quit")
	three := sessionID(t, before(t, second, "you> /resume"))

	inOrder(t, first, "you> /new\n", "Session "+one+" summarized: Greeting\n", "New session "+two+".",
		"you> /quit\n", "Session "+two+" summarized: Second greeting\n")
	inOrder(t, second, "Resumed session "+one, "Session "+three+" summarized: Third greeting\n", "you> /quit\n")
	if strings.Contains(second, "Session "+one+" summarized") {
		t.Errorf("the resumed session, unchanged, was summarized again:\n%s", second)
	}
	if n := len(summaries.Requests()); n != 3 {
		t.Errorf("the summarizer made %d requests, want 3", n)
	}
	if title := scalar[string](t, d, "select title from sessions where id = $1", one); title != "Greeting" {
		t.Errorf("title = %q, want Greeting", title)
	}
}

func TestConsole_APP15_ResumingTheSessionInUseDoesNotLeaveItAndTheEndOfInputLeavesIt(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	summaries := officinatest.NewModel("summarizer", summaryReply(t, "Greeting", "Sam said hello."),
		summaryReply(t, "Two greetings", "Sam said hello twice."))
	first := summarized(t, d, officinatest.NewModel("scripted", officinatest.TextReply("Hello.")), summaries, "Sam",
		"Hi.")
	id := sessionID(t, first)

	second := summarized(t, d, officinatest.NewModel("scripted", officinatest.TextReply("Hello again.")), summaries,
		"Sam", "/resume "+id, "Hi again.", "/resume "+id, "/quit")

	if !strings.HasSuffix(first, "Session "+id+" summarized: Greeting\n") {
		t.Errorf("the end of the input did not summarize the session:\n%s", first)
	}
	inOrder(t, second, "Hello again.", "you> /resume "+id+"\n", "Resumed session "+id, "you> /quit\n",
		"Session "+id+" summarized: Two greetings\n")
	if n := strings.Count(second, "summarized:"); n != 1 {
		t.Errorf("the session was summarized %d times, want once:\n%s", n, second)
	}
	if text := summaries.Requests()[1].Messages[0].Text(); !strings.Contains(text, "Staff: Hi again.") {
		t.Errorf("the second summary's transcript lacks the second message:\n%s", text)
	}
}

func TestConsole_APP15_ASessionLeftWithoutASummaryAfterACrashIsSummarizedWhenSessionsListsIt(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	id := crashMidReply(t, d)

	summaries := officinatest.NewModel("summarizer", summaryReply(t, "Restock of book 320 not confirmed",
		"Sam asked to restock book 320; the session ended before it was approved."))
	transcript := summarized(t, d, officinatest.NewModel("scripted"), summaries, "Sam", "/sessions", "/sessions",
		"/quit")

	inOrder(t, transcript, "you> /sessions\n", "Summarizing 1 session left without a summary…\n", "  "+id+"  ",
		"  Sam  Restock of book 320 not confirmed  $", "    Sam asked to restock book 320; the session ended",
		"you> /sessions\n", "  Sam  Restock of book 320 not confirmed  $")
	if n := len(summaries.Requests()); n != 1 {
		t.Errorf("the summarizer made %d requests, want 1", n)
	}
	text := summaries.Requests()[0].Messages[0].Text()
	inOrder(t, text, "Staff: Restock book 320 with 2.\n", `Tool call restock_book {"bookId":320,"quantity":2}`)
}

func TestConsole_APP15_AListingSummarizesAFewSessionsAtATimeAndSaysWhenASummaryFails(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	// Four sessions left without a summary, as by an application without a summarizer.
	for _, text := range []string{"One.", "Two.", "Three.", "Four."} {
		session(t, d, officinatest.NewModel("scripted", officinatest.TextReply(text)), "", "Sam", text, "/quit")
	}
	// The first summary is not the typed output; the next two are.
	summaries := officinatest.NewModel("summarizer", officinatest.TextReply(`{"title":"Four"}`),
		summaryReply(t, "Three", "Sam said three."), summaryReply(t, "Two", "Sam said two."),
		summaryReply(t, "One", "Sam said one."))

	transcript := summarized(t, d, officinatest.NewModel("scripted"), summaries, "Sam", "/sessions", "/sessions",
		"/sessions", "/quit")

	listings := strings.Split(transcript, "you> /sessions\n")
	if len(listings) != 4 {
		t.Fatalf("transcript has %d listings, want 3:\n%s", len(listings)-1, transcript)
	}
	inOrder(t, listings[1], "Summarizing 3 of 4 sessions left without a summary; /sessions again does more…\n",
		"could not be summarized: the output does not match its schema: /summary: is required; /changes: is required]",
		"  Sam  (no title yet)  $", "  Sam  Three  $", "  Sam  Two  $", "  Sam  (no title yet)  $")
	// The failed session is not tried again in this console.
	inOrder(t, listings[2], "Summarizing 1 session left without a summary…\n", "  Sam  One  $")
	if strings.Contains(listings[3], "Summarizing") {
		t.Errorf("the third listing summarized again:\n%s", listings[3])
	}
	if n := len(summaries.Requests()); n != 4 {
		t.Errorf("the summarizer made %d requests, want 4", n)
	}
}
