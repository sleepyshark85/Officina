package officinatest_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

func message(role officina.Role, text string) officina.Message {
	return officina.Message{Role: role, Blocks: []officina.Block{{Text: text}}}
}

// stream sends req to model and returns its events and the error that ended them.
func stream(t *testing.T, model *officinatest.Model, req officina.Request) ([]officina.ModelEvent, error) {
	t.Helper()
	var events []officina.ModelEvent
	for event, err := range model.Stream(t.Context(), req) {
		if err != nil {
			return events, err
		}
		events = append(events, event)
	}
	return events, nil
}

func TestModel_TEST01_RepliesInOrderAndRecordsRequests(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply("One."), officinatest.TextReply("Two."))
	first := officina.Request{Instructions: "A", Messages: []officina.Message{message(officina.User, "Hi")}}
	second := officina.Request{Instructions: "A", Messages: []officina.Message{
		message(officina.User, "Hi"), message(officina.Assistant, "One."), message(officina.User, "Again"),
	}}

	got1, err1 := stream(t, model, first)
	got2, err2 := stream(t, model, second)
	_, err3 := stream(t, model, first)

	if err1 != nil || err2 != nil {
		t.Fatalf("Stream() errors = %v, %v; want nil", err1, err2)
	}
	if diff := cmp.Diff(officinatest.TextReply("One.").Events, got1); diff != "" {
		t.Errorf("first reply mismatch (-want +got):\n%s", diff)
	}
	if diff := cmp.Diff(officinatest.TextReply("Two.").Events, got2); diff != "" {
		t.Errorf("second reply mismatch (-want +got):\n%s", diff)
	}
	if err3 == nil || err3.Error() != "scripted model: request 3 has no reply left" {
		t.Errorf("third Stream() error = %v, want no reply left", err3)
	}
	if diff := cmp.Diff([]officina.Request{first, second, first}, model.Requests()); diff != "" {
		t.Errorf("requests mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_TEST01_RejectsRoleSequencesTheProviderRejectsAndKeepsTheReply(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name  string
		roles []officina.Role
		want  string
	}{
		{"empty", nil, "the first message is not the user's"},
		{"assistant first", []officina.Role{officina.Assistant}, "the first message is not the user's"},
		{"two user messages", []officina.Role{officina.User, officina.User}, "messages 1 and 2 have one role, user"},
		{
			"user after operator", []officina.Role{officina.User, officina.Operator, officina.User},
			"operator message 2 is followed by a user message",
		},
		{
			"operator after assistant", []officina.Role{officina.User, officina.Assistant, officina.Operator},
			"operator message 3 does not follow a user message",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
			var messages []officina.Message
			for _, role := range tt.roles {
				messages = append(messages, message(role, "x"))
			}

			_, err := stream(t, model, officina.Request{Messages: messages})
			events, valid := stream(t, model, officina.Request{Messages: []officina.Message{message(officina.User, "Hi")}})

			if err == nil || !strings.HasSuffix(err.Error(), tt.want) {
				t.Errorf("Stream() error = %v, want one ending %q", err, tt.want)
			}
			if valid != nil || len(events) != 3 {
				t.Errorf("next valid request got %d events and error %v, want the kept reply", len(events), valid)
			}
		})
	}
}

func TestCheckPrefix_TEST02_ReportsEachKindOfChange(t *testing.T) {
	t.Parallel()
	hi := message(officina.User, "Hi")
	hello := officina.Message{Role: officina.Assistant, Blocks: []officina.Block{officinatest.TextBlock("Hello.")}}
	search := officina.Tool{Name: "search", Description: "Searches.", InputSchema: jsontext.Value(`{"type":"object"}`)}
	changed := search
	changed.InputSchema = jsontext.Value(`{"type": "object"}`)
	requests := []officina.Request{
		{Tools: []officina.Tool{search}, Instructions: "A", Messages: []officina.Message{hi}},
		{Tools: []officina.Tool{search}, Instructions: "A", Messages: []officina.Message{hi, hello}},
		{Tools: []officina.Tool{changed}, Instructions: "A", Messages: []officina.Message{hi, hello}},
		{Tools: []officina.Tool{changed}, Instructions: "B", Messages: []officina.Message{hi, hello}},
		{Tools: []officina.Tool{changed}, Instructions: "B", Messages: []officina.Message{hi, {
			Role: officina.Assistant, Blocks: []officina.Block{{Text: "Hello.", Raw: jsontext.Value(`{"type": "text", "text": "Hello."}`)}},
		}}},
		{Tools: []officina.Tool{changed}, Instructions: "B"},
	}

	err := officinatest.CheckPrefix(requests)

	want := []string{
		"request 3: the tools differ from request 2's",
		"request 4: the instructions differ from request 3's",
		"request 5: message 2 differs from request 4's",
		"request 6: has 0 messages, fewer than the 2 from request 5's",
	}
	if err == nil {
		t.Fatal("CheckPrefix() = nil, want the differences")
	}
	if diff := cmp.Diff(want, strings.Split(err.Error(), "\n")); diff != "" {
		t.Errorf("problems mismatch (-want +got):\n%s", diff)
	}
	if err := officinatest.CheckPrefix(requests[:2]); err != nil {
		t.Errorf("CheckPrefix() of a stable prefix = %v, want nil", err)
	}
}

func TestTextBlock_TEST01_StoresRawJSONInTheCanonicalForm(t *testing.T) {
	t.Parallel()

	got := officinatest.TextBlock(`Café <b> & "q"`)

	want := officina.Block{Text: `Café <b> & "q"`, Raw: jsontext.Value(`{"type":"text","text":"Café \u003cb\u003e \u0026 \"q\""}`)}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("TextBlock() mismatch (-want +got):\n%s", diff)
	}
}

func TestCheckPrefix_TEST02_ComparesToolCallsAndResults(t *testing.T) {
	t.Parallel()
	hi := message(officina.User, "Hi")
	call := func(input string) officina.Message {
		b := officinatest.ToolUseBlock("c1", "search", `{}`)
		b.ToolCall.Input = jsontext.Value(input)
		return officina.Message{Role: officina.Assistant, Blocks: []officina.Block{b}}
	}
	result := func(content string) officina.Message {
		return officina.Message{Role: officina.User, Blocks: []officina.Block{
			{ToolResult: &officina.ToolResult{CallID: "c1", Content: content}},
		}}
	}
	tests := []struct {
		name          string
		first, second []officina.Message
		same          bool
	}{
		{"same", []officina.Message{hi, call(`{}`), result("ok")}, []officina.Message{hi, call(`{}`), result("ok")}, true},
		{"call input", []officina.Message{hi, call(`{}`)}, []officina.Message{hi, call(`{"q":1}`)}, false},
		{"call against none", []officina.Message{hi, call(`{}`)}, []officina.Message{hi, message(officina.Assistant, "")}, false},
		{"result content", []officina.Message{hi, call(`{}`), result("ok")}, []officina.Message{hi, call(`{}`), result("no")}, false},
		{"result against none", []officina.Message{hi, call(`{}`), result("")}, []officina.Message{hi, call(`{}`), {
			Role: officina.User, Blocks: []officina.Block{{}},
		}}, false},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			err := officinatest.CheckPrefix([]officina.Request{{Messages: tt.first}, {Messages: tt.second}})

			if (err == nil) != tt.same {
				t.Errorf("CheckPrefix() = %v, want the prefix same = %v", err, tt.same)
			}
		})
	}
}

func TestCheckConversation_TEST07_AnswersEveryToolCallOnceInOrder(t *testing.T) {
	t.Parallel()
	hi := message(officina.User, "Hi")
	calls := officina.Message{Role: officina.Assistant, Blocks: []officina.Block{
		officinatest.ToolUseBlock("c1", "search", `{}`), officinatest.ToolUseBlock("c2", "search", `{}`),
	}}
	answer := func(ids ...string) officina.Message {
		m := officina.Message{Role: officina.User}
		for _, id := range ids {
			m.Blocks = append(m.Blocks, officina.Block{ToolResult: &officina.ToolResult{CallID: id, Content: "ok"}})
		}
		return m
	}
	done := message(officina.Assistant, "Done.")
	tests := []struct {
		name     string
		messages []officina.Message
		want     string
	}{
		{"answered", []officina.Message{hi, calls, answer("c1", "c2"), done}, ""},
		{"user message after results", []officina.Message{hi, calls, answer("c1", "c2"), hi, done}, ""},
		{"operator after results", []officina.Message{
			hi, calls, answer("c1", "c2"), hi, message(officina.Operator, "Monday."), done,
		}, ""},
		{"unanswered", []officina.Message{hi, calls}, `the calls ["c1" "c2"] of the last message have no results`},
		{"answered by text", []officina.Message{hi, calls, hi}, `message 3 answers calls [], want ["c1" "c2"]`},
		{"out of order", []officina.Message{hi, calls, answer("c2", "c1")}, `message 3 answers calls ["c2" "c1"]`},
		{"twice", []officina.Message{hi, calls, answer("c1", "c2", "c2")}, `answers calls ["c1" "c2" "c2"]`},
		{"answering nothing", []officina.Message{hi, done, answer("c1")}, `message 3 answers calls ["c1"], want []`},
		{"two text messages", []officina.Message{hi, hi}, "messages 1 and 2 have one role, user"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			err := officinatest.CheckConversation(tt.messages)

			if tt.want == "" && err != nil || tt.want != "" && (err == nil || !strings.Contains(err.Error(), tt.want)) {
				t.Errorf("CheckConversation() = %v, want %q", err, tt.want)
			}
		})
	}
}

func TestApprover_TEST01_AnswersInOrderAndRecordsEachCall(t *testing.T) {
	t.Parallel()
	approver := officinatest.NewApprover(officina.Approval{Approved: true}, officina.Approval{Reason: "no"})
	call := func(id string) officina.ToolCall { return officina.ToolCall{ID: id, Name: "order"} }

	first, err1 := approver.Approve(t.Context(), officina.Tool{}, call("c1"))
	second, err2 := approver.Approve(t.Context(), officina.Tool{}, call("c2"))
	_, err3 := approver.Approve(t.Context(), officina.Tool{}, call("c3"))

	if err1 != nil || err2 != nil || !first.Approved || second != (officina.Approval{Reason: "no"}) {
		t.Errorf("answers = %+v, %v and %+v, %v; want approved, then denied with its reason", first, err1, second, err2)
	}
	if err3 == nil || err3.Error() != "scripted approver: request 3 has no answer left" {
		t.Errorf("third Approve() error = %v, want no answer left", err3)
	}
	if diff := cmp.Diff([]officina.ToolCall{call("c1"), call("c2"), call("c3")}, approver.Asked()); diff != "" {
		t.Errorf("asked mismatch (-want +got):\n%s", diff)
	}
}

func TestToolUseBlock_TEST01_StoresTheCallAndItsRawJSON(t *testing.T) {
	t.Parallel()

	got := officinatest.ToolUseBlock("c1", "search", `{"q":"<b>"}`)
	invalid := officinatest.ToolUseBlock("c2", "search", `{"q":`)

	want := `{"type":"tool_use","id":"c1","name":"search","input":{"q":"` + htmlEscaped("<b>") + `"}}`
	if string(got.Raw) != want || string(got.ToolCall.Input) != `{"q":"<b>"}` || got.ToolCall.ID != "c1" {
		t.Errorf("ToolUseBlock() = %s with call %+v, want %s and the input as given", got.Raw, got.ToolCall, want)
	}
	if string(invalid.Raw) != `{"type":"tool_use","id":"c2","name":"search","input":{}}` ||
		string(invalid.ToolCall.Input) != `{"q":` {
		t.Errorf("ToolUseBlock() of invalid input = %s with input %s", invalid.Raw, invalid.ToolCall.Input)
	}
}

// htmlEscaped returns s as the inside of a JSON string with <, > and & escaped.
func htmlEscaped(s string) string {
	quoted, err := json.Marshal(s, jsontext.EscapeForHTML(true))
	if err != nil {
		panic(err)
	}
	return string(quoted[1 : len(quoted)-1])
}
