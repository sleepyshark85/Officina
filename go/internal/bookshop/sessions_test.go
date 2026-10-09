package bookshop_test

import (
	"bufio"
	"context"
	"encoding/json/v2"
	"errors"
	"io"
	"math"
	"os"
	"os/exec"
	"regexp"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// Sessions, the status line and budgets, end to end. Each session is an application start: what one leaves
// behind, the next finds only in the database.

// say returns a reply that streams text and reports usage.
func say(text string, usage officina.Usage) officinatest.Reply {
	return officinatest.Reply{Events: []officina.ModelEvent{
		officina.TextDelta{Text: text}, officina.BlockReceived{Block: officinatest.TextBlock(text)},
		officina.UsageReceived{Usage: usage}, officina.Finished{Reason: officina.FinishEnd},
	}}
}

// withUsage returns reply with usage reported just before it finishes.
func withUsage(reply officinatest.Reply, usage officina.Usage) officinatest.Reply {
	last := len(reply.Events) - 1
	reply.Events = append(reply.Events[:last:last], officina.UsageReceived{Usage: usage}, reply.Events[last])
	return reply
}

var sessionLine = regexp.MustCompile(`(?m)^(?:New s|S)ession ([0-9a-f]{12})\.$`)

// sessionID returns the id of the session a transcript started last.
func sessionID(t *testing.T, transcript string) string {
	t.Helper()
	found := sessionLine.FindAllStringSubmatch(transcript, -1)
	if found == nil {
		t.Fatalf("transcript names no session:\n%s", transcript)
	}
	return found[len(found)-1][1]
}

// stored returns the conversation stored for session id.
func stored(t *testing.T, d *database, id string) *officina.Conversation {
	t.Helper()
	var c officina.Conversation
	if err := json.Unmarshal([]byte(scalar[string](t, d, "select conversation from sessions where id = $1", id)), &c); err != nil {
		t.Fatalf("unmarshal session %s: %v", id, err)
	}
	return &c
}

func TestConsole_APP10_QuitRestartAndResumeContinuesTheSessionWithItsPrefixByteIdentical(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	before := officinatest.NewModel("scripted",
		sayThenCall("Checking.", officinatest.ToolUseBlock("c1", "get_book", `{"bookId":144}`)),
		officinatest.TextReply("The Winter Archive is in stock."))
	first := session(t, d, before, "", "Sam", "Is book 144 in stock?", "/quit")
	id := sessionID(t, first)

	after := officinatest.NewModel("scripted", officinatest.TextReply("It costs £6.28."))
	second := session(t, d, after, "", "Sam", "/sessions", "/resume "+id, "What does it cost?", "/quit")

	inOrder(t, second, "Sessions, most recent first:\n", "  "+id+"  ", "  Sam  (no title yet)  $",
		"you> /resume "+id+"\n", "Resumed session "+id+": 5 messages", "It costs £6.28.")
	if err := officinatest.CheckPrefix(append(before.Requests(), after.Requests()...)); err != nil {
		t.Errorf("the prefix changed across the restart: %v", err)
	}
	// The run context was sent when the session started, and the day and staff member have not changed since.
	want := []officina.Role{officina.User, officina.Operator, officina.Assistant, officina.User, officina.Assistant,
		officina.User}
	if diff := cmp.Diff(want, roles(after.Requests()[0].Messages)); diff != "" {
		t.Errorf("first request after the restart (-want +got):\n%s", diff)
	}
	if n := len(stored(t, d, id).Messages()); n != 7 {
		t.Errorf("the stored session has %d messages, want 7", n)
	}
}

// crashEnv names the variable that makes the test binary the application that crashes, not the tests.
const crashEnv = "BOOKSHOP_TEST_CRASH_DATABASE"

// crashingApp runs as the child process of the crash test: the application, against the database the variable
// names, with a model that asks to restock a book and reports usage. The parent kills it at the approval prompt.
func crashingApp() int {
	model := officinatest.NewModel("scripted", withUsage(
		sayThenCall("I'll add two copies.", officinatest.ToolUseBlock("c1", "restock_book", `{"bookId":320,"quantity":2}`)),
		officina.Usage{Input: 100, Output: 50, CacheRead: 900, CacheWrite: 200}))
	app, err := bookshop.Build(context.Background(), bookshop.Config{
		Database: os.Getenv(crashEnv), Model: priced{model}, In: os.Stdin, Out: os.Stdout, Echo: true,
	})
	if err != nil {
		return 1
	}
	defer app.Close()
	if app.Run(context.Background()) != nil {
		return 1
	}
	return 0
}

func TestConsole_APP10_ACrashMidReplyLosesAtMostTheStepInFlightAndTheSessionResumes(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	stock := d.stock(t, 320)

	// The application is killed at the approval prompt: after the model's reply was saved, before its tool ran.
	child := exec.CommandContext(t.Context(), os.Args[0])
	child.Env = append(os.Environ(), crashEnv+"="+d.url)
	in, err := child.StdinPipe()
	if err != nil {
		t.Fatalf("StdinPipe() error = %v", err)
	}
	out, err := child.StdoutPipe()
	if err != nil {
		t.Fatalf("StdoutPipe() error = %v", err)
	}
	if err := child.Start(); err != nil {
		t.Fatalf("Start() error = %v", err)
	}
	if _, err := io.WriteString(in, "Sam\nRestock book 320 with 2.\n"); err != nil {
		t.Fatalf("write to the application: %v", err)
	}
	if err := waitFor(out, "Approve? [y/N] "); err != nil {
		t.Fatalf("the application never asked for approval: %v", err)
	}
	if err := child.Process.Kill(); err != nil {
		t.Fatalf("Kill() error = %v", err)
	}
	_ = child.Wait() // It was killed, so it fails.

	id := scalar[string](t, d, "select id from sessions order by updated desc limit 1")
	saved := stored(t, d, id).Messages()
	if diff := cmp.Diff([]officina.Role{officina.User, officina.Operator, officina.Assistant}, roles(saved)); diff != "" {
		t.Fatalf("saved roles mismatch (-want +got):\n%s", diff)
	}

	after := officinatest.NewModel("scripted", officinatest.TextReply("The restock did not finish; shall I try again?"))
	transcript := session(t, d, after, "", "Sam", "/resume "+id, "Did it work?", "/cost", "/quit")

	// The crashed reply's model call ($0.00258) is in the session's totals, though the reply never ended.
	inOrder(t, transcript,
		"Resumed session "+id+": 3 messages, $0.0026 so far.",
		"The restock did not finish",
		"Session "+id+": tokens: 1,200 in (75% from cache), 50 out; cost $0.0026 of its $5.00 budget.")
	var got []officina.ToolResult
	for _, b := range after.Requests()[0].Messages[3].Blocks {
		if b.ToolResult != nil {
			got = append(got, *b.ToolResult)
		}
	}
	if len(got) != 1 || !got[0].IsError || !strings.Contains(got[0].Content, "interrupted") {
		t.Errorf("results after the crash = %+v, want one interrupted error result", got)
	}
	if err := officinatest.CheckConversation(after.Requests()[0].Messages); err != nil {
		t.Errorf("the first request after the crash is invalid: %v", err)
	}
	if got := d.stock(t, 320); got != stock {
		t.Errorf("stock = %d, want %d: the restock never ran", got, stock)
	}
}

// waitFor reads r until it has read text.
func waitFor(r io.Reader, text string) error {
	var read strings.Builder
	br := bufio.NewReader(r)
	for !strings.Contains(read.String(), text) {
		b, err := br.ReadByte()
		if err != nil {
			return errors.Join(err, errors.New("read: "+read.String()))
		}
		read.WriteByte(b)
	}
	return nil
}

func TestConsole_APP10_ASessionWhoseAgentChangedIsRefusedAndANewOneIsOffered(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	id := sessionID(t, session(t, d, officinatest.NewModel("scripted", officinatest.TextReply("Hello.")), "", "Sam",
		"Hi.", "/quit"))

	changed := officinatest.NewModel("scripted, effort high", officinatest.TextReply("Hello, new session."))
	transcript := session(t, d, changed, "", "Sam", "/resume "+id, "Hi again.", "/quit")

	inOrder(t, transcript, "Session "+id+" was started with another version of the assistant, so it cannot go on. "+
		"Type /new to start a new session.", "Hello, new session.")
	if diff := cmp.Diff([]officina.Role{officina.User, officina.Operator}, roles(changed.Requests()[0].Messages)); diff != "" {
		t.Errorf("the reply did not start a new session (-want +got):\n%s", diff)
	}
}

func TestConsole_APP02_NewStartsASessionOfItsOwnAndResumeOfAnUnknownOrUnreadableIdSaysSo(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	if _, err := d.pool.Exec(t.Context(), `insert into sessions (id, staff_member, conversation, input_tokens,
		output_tokens, cache_read_tokens, cache_write_tokens, cost, updated)
		values ('broken-0001', 'Sam', 'not json', 0, 0, 0, 0, 0, now())`); err != nil {
		t.Fatalf("store a broken session: %v", err)
	}
	model := officinatest.NewModel("scripted", officinatest.TextReply("One."), officinatest.TextReply("Two."))

	transcript := session(t, d, model, "", "Sam", "First.", "/new", "/resume nosuchid", "/resume", "/resume broken-0001",
		"Second.", "/quit")

	newAt := strings.Index(transcript, "you> /new")
	if newAt < 0 {
		t.Fatalf("transcript lacks /new:\n%s", transcript)
	}
	first, second := sessionID(t, transcript[:newAt]), sessionID(t, transcript)
	if first == second {
		t.Fatalf("/new kept session %s", first)
	}
	inOrder(t, transcript, "New session "+second+".", "There is no session nosuchid. Type /sessions to list them.",
		"Which session? Type /resume <id>; /sessions lists them.",
		"The session could not be read: session broken-0001 cannot be read: ", "Two.")
	if diff := cmp.Diff([]officina.Role{officina.User, officina.Operator}, roles(model.Requests()[1].Messages)); diff != "" {
		t.Errorf("the reply after /new (-want +got):\n%s", diff)
	}
	if !strings.Contains(scalar[string](t, d, "select conversation from sessions where id = $1", first), "First.") ||
		strings.Contains(scalar[string](t, d, "select conversation from sessions where id = $1", second), "First.") {
		t.Error("the first message is not in the first session alone")
	}
}

func TestConsole_APP14_TheStatusLineAndCostShowTheTokensCacheShareAndCostOfTheReplyAndTheSession(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	// At Opus 5.5's price: 100 × $4 + 50 × $20 + 900 × $0.20 + 200 × $5 per million = $0.00258; then $0.00046.
	model := officinatest.NewModel("scripted",
		say("Hello.", officina.Usage{Input: 100, Output: 50, CacheRead: 900, CacheWrite: 200}),
		say("Again.", officina.Usage{Input: 10, Output: 10, CacheRead: 1_100}))

	transcript := session(t, d, model, "", "Sam", "Hi.", "Hi again.", "/cost", "/quit")

	id := sessionID(t, transcript)
	inOrder(t, transcript,
		"Hello.\n[tokens: 1,200 in (75% from cache), 50 out · reply $0.0026 · session $0.0026]\n",
		"Again.\n[tokens: 1,110 in (99% from cache), 10 out · reply $0.0005 · session $0.0030]\n",
		"you> /cost\n",
		"Session "+id+": tokens: 2,310 in (87% from cache), 60 out; cost $0.0030 of its $5.00 budget.\n")
	if got := scalar[float64](t, d, "select cost::float8 from sessions where id = $1", id); math.Abs(got-0.00304) > 1e-12 {
		t.Errorf("stored cost = %v, want 0.00304", got)
	}
}

func TestConsole_APP14_AReplyThatReachesItsBudgetStopsAndSaysWhyAndItsOutputLimitIsLoweredToWhatIsLeft(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		withUsage(sayThenCall("Checking.", officinatest.ToolUseBlock("c1", "get_book", `{"bookId":144}`)),
			officina.Usage{Input: 100, Output: 50}),
		officinatest.TextReply("Unused."))

	transcript := sessionOf(t, bookshop.Config{Budgets: bookshop.Budgets{Reply: 0.001, Session: 1}}, newDatabase(t),
		model, "", "Sam", "Is book 144 in stock?", "/quit")

	inOrder(t, transcript, "  < get_book: ok\n", "[Stopped: this reply has reached its budget of $0.001.]\n",
		"[tokens: 100 in (0% from cache), 50 out · reply $0.0014 · session $0.0014]")
	if requests := model.Requests(); len(requests) != 1 || requests[0].MaxOutputTokens != 50 {
		t.Errorf("requests = %d, the first's output limit %d; want one, of 50", len(requests), requests[0].MaxOutputTokens)
	}
}

func TestConsole_APP14_ASessionThatReachesItsBudgetStopsTheNextReplyBeforeAnyModelCall(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted",
		say("Hello.", officina.Usage{Input: 100, Output: 50, CacheRead: 900, CacheWrite: 200}),
		officinatest.TextReply("Unused."))

	transcript := sessionOf(t, bookshop.Config{Budgets: bookshop.Budgets{Reply: 1, Session: 0.002}}, newDatabase(t),
		model, "", "Sam", "Hi.", "Hi again.", "/quit")

	inOrder(t, transcript, "Hello.", "you> Hi again.\n",
		"[Stopped: this session has reached its budget of $0.002. Type /new to start a new session.]\n")
	if requests := model.Requests(); len(requests) != 1 || requests[0].MaxOutputTokens != 100 {
		t.Errorf("requests = %d, the first's output limit %d; want one, of 100", len(requests), requests[0].MaxOutputTokens)
	}
}

// conversationOf returns a conversation of id whose messages alternate between the user and the assistant.
func conversationOf(t *testing.T, id string, texts ...string) *officina.Conversation {
	t.Helper()
	type block struct {
		Text string `json:"text"`
	}
	type message struct {
		Role   string  `json:"role"`
		Blocks []block `json:"blocks"`
	}
	wire := struct {
		ID       string    `json:"id"`
		Messages []message `json:"messages"`
	}{ID: id}
	for i, text := range texts {
		role := "user"
		if i%2 == 1 {
			role = "assistant"
		}
		wire.Messages = append(wire.Messages, message{role, []block{{text}}})
	}
	data, err := json.Marshal(wire)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	var c officina.Conversation
	if err := json.Unmarshal(data, &c); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	return &c
}

func TestSessions_APP10_ASaveNeverOverwritesAnotherConsolesChangesOrAnotherSession(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	store := bookshop.NewSessions(d.pool)
	save := func(t *testing.T, c *officina.Conversation, staffMember string, cost float64, previous string) (string, error) {
		t.Helper()
		return store.Save(t.Context(), c, staffMember, officina.Usage{}, cost, previous)
	}
	mustSave := func(t *testing.T, c *officina.Conversation, previous string) string {
		t.Helper()
		saved, err := save(t, c, "Sam", 0, previous)
		if err != nil {
			t.Fatalf("Save() error = %v", err)
		}
		return saved
	}

	t.Run("a stale copy", func(t *testing.T) {
		t.Parallel()
		first := mustSave(t, conversationOf(t, "shared-0001", "Hi."), "")
		theirs := mustSave(t, conversationOf(t, "shared-0001", "Hi.", "Hello from the other counter."), first)

		_, err := save(t, conversationOf(t, "shared-0001", "Hi.", "Hello from here."), "Sam", 0, first)

		if !errors.Is(err, bookshop.ErrSessionChanged) {
			t.Errorf("Save() error = %v, want ErrSessionChanged", err)
		}
		if got := scalar[string](t, d, "select conversation from sessions where id = 'shared-0001'"); got != theirs {
			t.Errorf("stored = %s, want the other console's %s", got, theirs)
		}
	})
	t.Run("an earlier save that landed unseen", func(t *testing.T) {
		t.Parallel()
		first := mustSave(t, conversationOf(t, "landed-0001", "Hi."), "")
		mustSave(t, conversationOf(t, "landed-0001", "Hi.", "Hello."), first)

		later := mustSave(t, conversationOf(t, "landed-0001", "Hi.", "Hello.", "Thanks."), first)

		if got := scalar[string](t, d, "select conversation from sessions where id = 'landed-0001'"); got != later {
			t.Errorf("stored = %s, want the later save %s", got, later)
		}
	})
	t.Run("a new session whose id is taken", func(t *testing.T) {
		t.Parallel()
		mustSave(t, conversationOf(t, "taken-0001", "Hi."), "")
		if _, err := save(t, conversationOf(t, "taken-0001", "Hello?"), "Kim", 0.5, ""); !errors.Is(err, bookshop.ErrSessionChanged) {
			t.Errorf("Save() error = %v, want ErrSessionChanged", err)
		}
		kept, err := store.Load(t.Context(), "taken-0001")
		if err != nil || kept.StaffMember != "Sam" || kept.Cost != 0 {
			t.Errorf("Load() = %+v, %v; want Sam's session as saved", kept, err)
		}
	})
}

func TestConsole_APP10_AConsoleWhoseSessionChangedElsewhereSaysSoAndDoesNotOverwriteIt(t *testing.T) {
	t.Parallel()
	d := newDatabase(t)
	// Another console saves the session between this console's replies, with a reply this console never saw.
	changeElsewhere := func() {
		if _, err := d.pool.Exec(t.Context(),
			"update sessions set conversation = replace(conversation, 'Hello.', 'Hello from the other counter.')"); err != nil {
			t.Errorf("change the session: %v", err)
		}
	}
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."), officinatest.TextReply("Hello again."))

	transcript := session(t, d, model, "", "Sam", "Hi.", changeElsewhere, "Again.", "/quit")

	id := sessionID(t, transcript)
	inOrder(t, transcript, "Hello again.", "[The session could not be saved: session "+id+" changed elsewhere since "+
		"it was last saved here, so it was not overwritten. Type /resume "+id+" to go on from what was saved.]")
	if strings.Contains(scalar[string](t, d, "select conversation from sessions where id = $1", id), "Again.") {
		t.Error("the save overwrote the other console's session")
	}
}
