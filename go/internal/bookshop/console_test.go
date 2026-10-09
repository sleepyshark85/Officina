package bookshop_test

import (
	"context"
	"fmt"
	"io"
	"strings"
	"testing"
	"testing/synctest"
	"time"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// The end-to-end tests drive the real console, agent, core and tools against the real database; only the model
// (scripted replies) and the staff member (scripted lines, which also answer the approval prompts) are faked.

// session runs a console session against d and returns its transcript. Each string of script is a line the staff
// member types; each func runs before the next line is read. When the transcript first holds cancelOn, the reply in
// progress is interrupted, as Ctrl+C does.
func session(t *testing.T, d *database, model *officinatest.Model, cancelOn string, script ...any) string {
	t.Helper()
	out := &transcript{cancelOn: cancelOn}
	app, err := bookshop.Build(t.Context(), bookshop.Config{
		Database: d.url, Model: model, In: &input{t: t, script: script}, Out: out, Echo: true,
		Interrupt: func(ctx context.Context) (context.Context, context.CancelFunc) {
			ctx, out.interrupt = context.WithCancel(ctx)
			return ctx, out.interrupt
		},
	})
	if err != nil {
		t.Fatalf("Build() error = %v", err)
	}
	defer app.Close()
	if err := app.Run(t.Context()); err != nil {
		t.Fatalf("Run() error = %v\ntranscript:\n%s", err, out)
	}
	return out.String()
}

// input is the staff member's scripted input: it reads as the script's lines, and runs each func of the script
// when the console asks for the line after it.
type input struct {
	t      *testing.T
	script []any
	rest   string
}

func (in *input) Read(p []byte) (int, error) {
	for in.rest == "" && len(in.script) > 0 {
		switch step := in.script[0].(type) {
		case string:
			in.rest = step + "\n"
		case func():
			step()
		default:
			in.t.Errorf("a script holds lines and funcs, not %T", step)
		}
		in.script = in.script[1:]
	}
	if in.rest == "" {
		return 0, io.EOF
	}
	n := copy(p, in.rest)
	in.rest = in.rest[n:]
	return n, nil
}

// transcript is the console's output. When it first holds cancelOn, it interrupts the reply in progress.
type transcript struct {
	text      strings.Builder
	cancelOn  string
	interrupt context.CancelFunc
}

func (tr *transcript) Write(p []byte) (int, error) {
	n, err := tr.text.Write(p)
	if tr.cancelOn != "" && strings.Contains(tr.text.String(), tr.cancelOn) && tr.interrupt != nil {
		tr.interrupt()
		tr.cancelOn = ""
	}
	return n, err
}

func (tr *transcript) String() string {
	return tr.text.String()
}

// inOrder checks that parts appear in transcript in this order.
func inOrder(t *testing.T, transcript string, parts ...string) {
	t.Helper()
	at := 0
	for _, part := range parts {
		found := strings.Index(transcript[at:], part)
		if found < 0 {
			t.Fatalf("transcript lacks, after byte %d: %q\ntranscript:\n%s", at, part, transcript)
		}
		at += found + len(part)
	}
}

// sayThenCall returns a reply that streams text, then calls the tools of calls (made by officinatest.ToolUseBlock).
func sayThenCall(text string, calls ...officina.Block) officinatest.Reply {
	reply := officinatest.ToolUseReply(calls...)
	reply.Events = append([]officina.ModelEvent{
		officina.TextDelta{Text: text}, officina.BlockReceived{Block: officinatest.TextBlock(text)},
	}, reply.Events...)
	return reply
}

// results returns the tool results the model received in request i.
func results(t *testing.T, model *officinatest.Model, i int) []officina.ToolResult {
	t.Helper()
	requests := model.Requests()
	if i < 0 {
		i += len(requests)
	}
	messages := requests[i].Messages
	var results []officina.ToolResult
	for _, b := range messages[len(messages)-1].Blocks {
		if b.ToolResult != nil {
			results = append(results, *b.ToolResult)
		}
	}
	return results
}

func roles(messages []officina.Message) []officina.Role {
	var roles []officina.Role
	for _, m := range messages {
		roles = append(roles, m.Role)
	}
	return roles
}

func TestConsole_APP01_TheReplyStreamsWithTextBetweenToolCallsAndEachToolWithItsInputAndOutcome(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		sayThenCall("Let me look that up.", officinatest.ToolUseBlock("c1", "search_books", `{"title":"Winter Archive"}`)),
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.TextDelta{Text: "We have "}, officina.TextDelta{Text: "12 copies."},
			officina.BlockReceived{Block: officinatest.TextBlock("We have 12 copies.")},
			officina.Finished{Reason: officina.FinishEnd},
		}})

	transcript := session(t, newDatabase(t), model, "", "Sam", "Do we have The Winter Archive?", "/quit")

	inOrder(t, transcript,
		"Who is using the assistant? Your name: Sam\nHello, Sam.\n",
		"you> Do we have The Winter Archive?\n",
		"assistant> Let me look that up.\n",
		`  > search_books {"title":"Winter Archive"}`+"\n",
		"  < search_books: ok\n",
		"We have 12 copies.\n",
		"you> /quit\n")
	if got := results(t, model, -1)[0]; got.IsError || !strings.Contains(got.Content, `"title":"The Winter Archive"`) {
		t.Errorf("search result = %+v, want The Winter Archive", got)
	}
}

func TestConsole_APP02_HelpAndUnknownCommandsAreAnsweredAndQuitLeaves(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted")

	transcript := session(t, newDatabase(t), model, "", "", "  ", "Sam", "/help", "", "/sessions", "/quit", "Not read.")

	inOrder(t, transcript,
		"Bookshop Assistant. Type /help for commands.\n",
		"Who is using the assistant? Your name: \n",
		"Who is using the assistant? Your name:   \n",
		"Who is using the assistant? Your name: Sam\n",
		"you> /help\nCommands:\n  /help   Show this help.\n  /quit   Leave the assistant.\n",
		"Ctrl+C stops a reply in progress.\n",
		"you> \nyou> /sessions\nUnknown command /sessions. Type /help for commands.\n",
		"you> /quit\n")
	if strings.Contains(transcript, "Not read.") || len(model.Requests()) != 0 {
		t.Errorf("the session went on after /quit, or called the model:\n%s", transcript)
	}
}

func TestConsole_APP03_CancellingStopsTheReplyAndTheSessionGoesOn(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		officinatest.Reply{Events: []officina.ModelEvent{
			officina.TextDelta{Text: "Let me think about every book "}, officina.TextDelta{Text: "we have ever sold..."},
			officina.BlockReceived{Block: officinatest.TextBlock("…")}, officina.Finished{Reason: officina.FinishEnd},
		}},
		officinatest.TextReply("Hello again."))

	transcript := session(t, newDatabase(t), model, "every book ", "Sam", "Tell me everything.", "Hello?", "/quit")

	inOrder(t, transcript, "assistant> Let me think about every book \n[Cancelled.]\n", "you> Hello?\n",
		"assistant> Hello again.\n")
	if strings.Contains(transcript, "we have ever sold") {
		t.Errorf("the cancelled reply went on streaming:\n%s", transcript)
	}
	// The cancelled exchange left nothing behind: the second request holds the new message and the run context.
	second := model.Requests()[1].Messages
	if diff := cmp.Diff([]officina.Role{officina.User, officina.Operator}, roles(second)); diff != "" {
		t.Errorf("second request's roles mismatch (-want +got):\n%s", diff)
	}
	if second[0].Text() != "Hello?" {
		t.Errorf("second request's message = %q, want Hello?", second[0].Text())
	}
}

func TestConsole_APP03_CancellingAtTheApprovalPromptStopsTheReplyAtOnceAndTheChangeIsNotMade(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	stock := d.stock(t, 320)
	model := officinatest.NewModel("scripted",
		sayThenCall("I'll add two copies.", officinatest.ToolUseBlock("c1", "restock_book", `{"bookId":320,"quantity":2}`)),
		officinatest.TextReply("Hello again."))

	transcript := session(t, d, model, "Approve? [y/N] ", "Sam", "Restock book 320 with 2.", "Hello?", "/quit")

	// The line typed after the interrupt is the next message, not an answer to the abandoned prompt.
	inOrder(t, transcript,
		"    Approve? [y/N] \n",
		"  < restock_book: error: The call was denied: the run was cancelled while waiting for approval\n",
		"[Cancelled.]\n",
		"you> Hello?\n",
		"assistant> Hello again.\n")
	if got := d.stock(t, 320); got != stock {
		t.Errorf("stock of 320 = %d, want %d", got, stock)
	}
}

func TestConsole_APP05_TheReadToolsOfOneReplyAllAnswerFromTheDatabase(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		sayThenCall("Looking.",
			officinatest.ToolUseBlock("c1", "find_customer", `{"nameOrEmail":"Alice Martin"}`),
			officinatest.ToolUseBlock("c2", "list_customer_orders", `{"customerId":1}`),
			officinatest.ToolUseBlock("c3", "get_book", `{"bookId":216}`),
			officinatest.ToolUseBlock("c4", "search_books", `{"genre":"Fantasy","inStock":true,"limit":2}`)),
		sayThenCall("And order 77.", officinatest.ToolUseBlock("c5", "get_order", `{"orderId":77}`)),
		officinatest.TextReply("Here is what I found."))

	transcript := session(t, newDatabase(t), model, "", "Sam", "What do we know about Alice Martin?", "/quit")

	reads := results(t, model, 1)
	want := []string{`"name":"Alice Martin"`, `"status":`, `"title":"The Hollow Island"`, `"id":144`}
	for i, r := range reads {
		if r.IsError || !strings.Contains(r.Content, want[i]) {
			t.Errorf("read %d = %+v, want one holding %s", i+1, r, want[i])
		}
	}
	if got := results(t, model, -1); len(got) != 1 || got[0].IsError {
		t.Errorf("get_order result = %+v, want one success", got)
	}
	inOrder(t, transcript, "  < find_customer: ok", "  < get_order: ok", "Here is what I found.")
}

func TestConsole_APP06_AWriteShowsItsExactInputForApprovalAndRunsOnlyIfApproved(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	stock300, stock301 := d.stock(t, 300), d.stock(t, 301)
	model := officinatest.NewModel("scripted",
		sayThenCall("I'll add five copies.", officinatest.ToolUseBlock("c1", "restock_book", `{"bookId":300,"quantity":5}`)),
		officinatest.TextReply("Done: five more copies."),
		sayThenCall("I'll add three copies.", officinatest.ToolUseBlock("c2", "restock_book", `{"bookId":301,"quantity":3}`)),
		officinatest.TextReply("Understood, I left the stock as it was."))

	transcript := session(t, d, model, "",
		"Sam", "Restock book 300 with 5.", "y", "Restock book 301 with 3.", "n", "/quit")

	inOrder(t, transcript,
		"  ? restock_book needs your approval. Its exact input:\n",
		`    {"bookId":300,"quantity":5}`+"\n",
		"    Approve? [y/N] y\n",
		"  < restock_book: ok\n",
		"Done: five more copies.",
		`    {"bookId":301,"quantity":3}`+"\n",
		"    Approve? [y/N] n\n",
		"  < restock_book: error: The call was denied: the staff member declined\n",
		"Understood, I left the stock as it was.")
	if got300, got301 := d.stock(t, 300), d.stock(t, 301); got300 != stock300+5 || got301 != stock301 {
		t.Errorf("stock = %d, %d; want %d, %d", got300, got301, stock300+5, stock301)
	}
	if got := results(t, model, -1); !got[0].IsError {
		t.Errorf("denied call's result = %+v, want an error", got[0])
	}
}

func TestConsole_APP07_NotEnoughStockComesBackAsAnErrorResultAndTheModelRecoversInTheSameReply(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	stock := d.stock(t, 310)
	order := func(id string, copies int) officina.Block {
		return officinatest.ToolUseBlock(id, "place_order",
			fmt.Sprintf(`{"customerId":1,"lines":[{"bookId":310,"quantity":%d}]}`, copies))
	}
	model := officinatest.NewModel("scripted",
		sayThenCall("Placing the order.", order("c1", stock+10)),
		sayThenCall(fmt.Sprintf("Only %d are in stock; I'll order those instead.", stock), order("c2", stock)),
		officinatest.TextReply(fmt.Sprintf("Ordered all %d copies.", stock)))

	transcript := session(t, d, model, "",
		"Sam", fmt.Sprintf("Order %d copies of book 310 for Alice.", stock+10), "y", "y", "/quit")

	refused := results(t, model, 1)
	if len(refused) != 1 || !refused[0].IsError || !strings.HasPrefix(refused[0].Content, "not enough stock for") {
		t.Errorf("first order's result = %+v, want not enough stock as an error", refused)
	}
	inOrder(t, transcript, "  < place_order: error: not enough stock", fmt.Sprintf("Only %d are in stock", stock),
		"  < place_order: ok", fmt.Sprintf("Ordered all %d copies.", stock))
	if got := d.stock(t, 310); got != 0 {
		t.Errorf("stock of 310 = %d, want 0", got)
	}
}

func TestConsole_APP09_AMultiStepRequestFindsSearchesOrdersAfterApprovalAndAnswers(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	stock144, stock216 := d.stock(t, 144), d.stock(t, 216)
	orders := scalar[int64](t, d, "select count(*) from orders")
	const alice = `{"nameOrEmail":"Alice Martin"}`
	const lines = `{"customerId":1,"lines":[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]}`
	model := officinatest.NewModel("scripted",
		sayThenCall("Let me find Alice and the cheapest fantasy books in stock.",
			officinatest.ToolUseBlock("c1", "find_customer", alice),
			officinatest.ToolUseBlock("c2", "search_books", `{"genre":"Fantasy","inStock":true,"limit":2}`)),
		sayThenCall("I'll order The Winter Archive and The Hollow Island for Alice Martin.",
			officinatest.ToolUseBlock("c3", "place_order", lines)),
		officinatest.TextReply("Order placed for Alice Martin: The Winter Archive (£6.28) and The Hollow Island "+
			"(£6.92). Total £13.20."))

	transcript := session(t, d, model, "",
		"Sam", "Order the two cheapest fantasy books in stock for Alice Martin and tell me the total", "y", "/quit")

	// The two reads run in parallel, so only each one's own lines are in order, both before the write.
	inOrder(t, transcript, "  > find_customer "+alice, "  < find_customer: ok", "  > place_order")
	inOrder(t, transcript, "  > search_books", "  < search_books: ok", "  > place_order")
	inOrder(t, transcript, "  > place_order", "    "+lines+"\n", "    Approve? [y/N] y\n", "  < place_order: ok\n",
		"Total £13.20.\n")
	if got := results(t, model, -1); len(got) != 1 || got[0].IsError || !strings.Contains(got[0].Content, `"total":13.20`) {
		t.Errorf("place_order result = %+v, want a total of 13.20", got)
	}
	got := []int64{int64(d.stock(t, 144)), int64(d.stock(t, 216)), scalar[int64](t, d, "select count(*) from orders")}
	if diff := cmp.Diff([]int64{int64(stock144 - 1), int64(stock216 - 1), orders + 1}, got); diff != "" {
		t.Errorf("stock of 144 and 216, and orders, mismatch (-want +got):\n%s", diff)
	}
	if total := scalar[string](t, d, "select total::text from orders order by id desc limit 1"); total != "13.20" {
		t.Errorf("stored total = %s, want 13.20", total)
	}
}

func TestConsole_APP13_TheRunContextNamesTheDateAndStaffMemberAndIsSentAgainOnlyOnANewDay(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	synctest.Test(t, func(t *testing.T) {
		// The bubble's clock: the messages come at 08:00 and 20:00 on one day, then at 08:00 the next, local time.
		now := time.Now()
		morning := time.Date(now.Year(), now.Month(), now.Day()+1, 8, 0, 0, 0, time.Local)
		wait := func(until time.Time) func() { return func() { time.Sleep(time.Until(until)) } }
		model := officinatest.NewModel("scripted",
			officinatest.TextReply("Good morning."), officinatest.TextReply("Good evening."),
			officinatest.TextReply("Good morning again."))

		session(t, d, model, "", "Sam", wait(morning), "Morning!", wait(morning.Add(12*time.Hour)), "Evening!",
			wait(morning.Add(24*time.Hour)), "Next morning!", "/quit")

		last := model.Requests()[2]
		var contexts []string
		for _, m := range last.Messages {
			if m.Role == officina.Operator {
				contexts = append(contexts, m.Text())
			}
		}
		want := []string{
			"Today is " + morning.Format("Monday 2 January 2006") + ". The staff member using the assistant is Sam.",
			"Today is " + morning.AddDate(0, 0, 1).Format("Monday 2 January 2006") +
				". The staff member using the assistant is Sam.",
		}
		if diff := cmp.Diff(want, contexts); diff != "" {
			t.Errorf("run contexts mismatch (-want +got):\n%s", diff)
		}
		wantRoles := []officina.Role{officina.User, officina.Operator, officina.Assistant, officina.User,
			officina.Assistant, officina.User, officina.Operator}
		if diff := cmp.Diff(wantRoles, roles(last.Messages)); diff != "" {
			t.Errorf("last request's roles mismatch (-want +got):\n%s", diff)
		}
		if strings.Contains(last.Instructions, "Sam") {
			t.Error("the instructions name the staff member")
		}
	})
}

func TestConsole_APP18_WithTheDatabaseDownToolsReturnErrorsAndOnceItIsBackTheSessionWorksAgain(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	model := officinatest.NewModel("scripted",
		sayThenCall("Checking.", officinatest.ToolUseBlock("c1", "get_book", `{"bookId":144}`)),
		officinatest.TextReply("I can't reach the database right now; please try again shortly."),
		sayThenCall("Checking again.", officinatest.ToolUseBlock("c2", "get_book", `{"bookId":144}`)),
		officinatest.TextReply("The Winter Archive is in stock."))
	d.takeDown(t)

	transcript := session(t, d, model, "",
		"Sam", "Is book 144 in stock?", func() { d.bringBack(t) }, "And now?", "/quit")

	inOrder(t, transcript, "  < get_book: error: ", "I can't reach the database", "  < get_book: ok",
		"The Winter Archive is in stock.")
	if got := results(t, model, 1); !got[0].IsError {
		t.Errorf("get_book result with the database down = %+v, want an error", got[0])
	}
	if got := results(t, model, -1); got[0].IsError {
		t.Errorf("get_book result once the database is back = %+v, want a success", got[0])
	}
}
