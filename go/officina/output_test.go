package officina_test

import (
	"context"
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"
	"pgregory.net/rapid"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

type line struct {
	BookID int   `json:"bookId" jsonschema:"The book's id."`
	Copies uint8 `json:"copies"`
}

type orderOutput struct {
	Customer string   `json:"customer"`
	Lines    []line   `json:"lines"`
	Gift     *line    `json:"gift"`
	Total    float64  `json:"total"`
	Tags     []string `json:"tags,omitempty"`
}

// typedAgent returns an agent of model that requires the typed output of T.
func typedAgent[T any](t *testing.T, model officina.Model) *officina.Agent {
	t.Helper()
	output, err := officina.NewOutput[T]()
	if err != nil {
		t.Fatalf("NewOutput() error = %v", err)
	}
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{Output: output})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return agent
}

func TestNewOutput_OUT01_SendsTheSchemaDerivedFromTheTypeAndReturnsTheReplyAsAValueOfIt(t *testing.T) {
	t.Parallel()
	reply := `{"customer":"Ana","lines":[{"bookId":144,"copies":2}],"gift":null,"total":12.56}`
	model := officinatest.NewModel("scripted", officinatest.TextReply(reply))

	res := run(t, typedAgent[orderOutput](t, model), nil, "Ana wants two copies of book 144.", officina.RunOptions{})

	want := officina.Result{Status: officina.Completed, Text: reply, Output: orderOutput{
		Customer: "Ana", Lines: []line{{BookID: 144, Copies: 2}}, Total: 12.56,
	}}
	if diff := cmp.Diff(want, res, outcome()); diff != "" {
		t.Errorf("result mismatch (-want +got):\n%s", diff)
	}
	schema := `{"type":"object","properties":{` +
		`"customer":{"type":"string"},` +
		`"lines":{"type":"array","items":{"type":"object","properties":{` +
		`"bookId":{"type":"integer","description":"The book's id."},"copies":{"type":"integer","minimum":0}},` +
		`"required":["bookId","copies"],"additionalProperties":false}},` +
		`"gift":{"type":["object","null"],"properties":{` +
		`"bookId":{"type":"integer","description":"The book's id."},"copies":{"type":"integer","minimum":0}},` +
		`"required":["bookId","copies"],"additionalProperties":false},` +
		`"total":{"type":"number"},` +
		`"tags":{"type":"array","items":{"type":"string"}}},` +
		`"required":["customer","lines","gift","total"],"additionalProperties":false}`
	if got := string(model.Requests()[0].OutputSchema); got != schema {
		t.Errorf("output schema = %s, want %s", got, schema)
	}
}

func TestNewOutput_OUT01_TEST08_RefusesATypeWithoutAClosedSchema(t *testing.T) {
	t.Parallel()
	type counts struct {
		Counts map[string]int `json:"counts"`
	}
	type nested struct {
		Rows []*counts `json:"rows"`
	}
	type embedded struct {
		line
	}
	tests := []struct {
		name string
		make func() (*officina.Output, error)
		want string
	}{
		{"a map", officina.NewOutput[counts], `"/properties/counts" is an open object`},
		{"a map nested in a slice", officina.NewOutput[nested], `"/properties/rows/items/properties/counts"`},
		{"not a struct", officina.NewOutput[[]line], "is not a struct"},
		{"a map at the root", officina.NewOutput[map[string]string], "is not a struct"},
		{"an embedded struct", officina.NewOutput[embedded], "not supported"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			output, err := tt.make()
			if err == nil || !strings.Contains(err.Error(), tt.want) {
				t.Errorf("NewOutput() = %v, %v; want an error containing %q", output, err, tt.want)
			}
		})
	}
}

func TestRun_OUT02_OutputThatFailsToDecodeOrValidateFailsTheRunWithTheErrors(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name, reply, detail string
	}{
		{"not JSON", "Ana wants two copies.", "the output could not be read as orderOutput: "},
		{"a property missing", `{"customer":"Ana","lines":[],"gift":null}`,
			"the output does not match its schema: /total: is required"},
		{"several problems", `{"customer":1,"lines":[{"bookId":1.5,"copies":2,"x":1}],"gift":null,"total":"9"}`,
			"the output does not match its schema: /customer: must be string; /lines/0/bookId: must be integer; " +
				"/lines/0/x: is not allowed; /total: must be number"},
		{"a value the type cannot hold", `{"customer":"Ana","lines":[{"bookId":1,"copies":300}],"gift":null,"total":1}`,
			"the output could not be read as orderOutput: "},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			model := officinatest.NewModel("scripted", officinatest.TextReply(tt.reply),
				officinatest.TextReply(`{"customer":"Ana","lines":[],"gift":null,"total":0}`))
			var c officina.Conversation

			res := run(t, typedAgent[orderOutput](t, model), &c, "Read the order.", officina.RunOptions{})

			if res.Status != officina.Failed || res.Failure != officina.InvalidOutput || res.Output != nil ||
				res.Text != "" || !strings.HasPrefix(res.Detail, tt.detail) {
				t.Errorf("result = %+v, want Failed(InvalidOutput) with detail %q", res, tt.detail)
			}
			// There is no correction round, and the reply stays in the conversation.
			if n := len(model.Requests()); n != 1 {
				t.Errorf("the run made %d model calls, want 1", n)
			}
			if n := len(c.Messages()); n != 2 {
				t.Errorf("the conversation has %d messages, want 2", n)
			}
		})
	}
}

func TestRun_GEN05_ARunWithoutTypedOutputReturnsItsTextAndNoOutput(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply(`{"customer":"Ana"}`))

	res := run(t, newAgent(t, model), nil, "Hi", officina.RunOptions{})

	if res.Status != officina.Completed || res.Text != `{"customer":"Ana"}` || res.Output != nil {
		t.Errorf("result = %+v, want Completed with the text and no output", res)
	}
	if schema := model.Requests()[0].OutputSchema; schema != nil {
		t.Errorf("output schema = %s, want none", schema)
	}
}

func TestAgent_CTX04_OUT01_TheOutputSchemaIsPartOfThePrefix(t *testing.T) {
	t.Parallel()
	model := officinatest.NewModel("scripted", officinatest.TextReply(`{"text":"a"}`),
		officinatest.TextReply(`{"text":"b"}`))
	type note struct {
		Text string `json:"text"`
	}
	typed, plain := typedAgent[note](t, model), newAgent(t, model)
	var c officina.Conversation

	run(t, typed, &c, "Hi", officina.RunOptions{})
	stopped := run(t, plain, &c, "Hi", officina.RunOptions{})
	again := run(t, typed, &c, "Hi again", officina.RunOptions{})

	if plain.CanContinue(&c) || !typed.CanContinue(&c) {
		t.Errorf("CanContinue() = %v without typed output, %v with it; want false, true", plain.CanContinue(&c),
			typed.CanContinue(&c))
	}
	if stopped.Failure != officina.PrefixMismatch {
		t.Errorf("run without typed output = %+v, want Failed(PrefixMismatch)", stopped)
	}
	if again.Output != (note{Text: "b"}) {
		t.Errorf("output = %#v, want the second note", again.Output)
	}
	if err := officinatest.CheckPrefix(model.Requests()); err != nil {
		t.Errorf("the prefix changed: %v", err)
	}
}

// replyText generates a reply's text for orderOutput: often valid, often not by one property, sometimes not JSON.
func replyText(t *rapid.T) string {
	if rapid.IntRange(0, 9).Draw(t, "prose") == 0 {
		return rapid.StringMatching(`[a-z {}":,]{0,12}`).Draw(t, "text")
	}
	lineGen := rapid.Custom(func(t *rapid.T) map[string]any {
		return map[string]any{
			"bookId": rapid.OneOf(rapid.Just[any](7.0), rapid.Just[any](1.5), rapid.Just[any]("7")).Draw(t, "bookId"),
			"copies": rapid.OneOf(rapid.Just[any](2.0), rapid.Just[any](-1.0), rapid.Just[any](300.0)).Draw(t, "copies"),
		}
	})
	order := map[string]any{
		"customer": rapid.OneOf(rapid.Just[any]("Ana"), rapid.Just[any](nil)).Draw(t, "customer"),
		"lines":    rapid.SliceOfN(lineGen, 0, 2).Draw(t, "lines"),
		"gift":     rapid.OneOf(rapid.Just[any](nil), lineGen.AsAny()).Draw(t, "gift"),
		"total":    rapid.OneOf(rapid.Just[any](12.5), rapid.Just[any](true)).Draw(t, "total"),
		"tags":     rapid.OneOf(rapid.Just[any]([]any{"a"}), rapid.Just[any]([]any{1.0})).Draw(t, "tags"),
		"extra":    1.0,
	}
	for _, name := range []string{"customer", "lines", "gift", "total", "tags", "extra"} {
		if rapid.IntRange(0, 3).Draw(t, "drop "+name) == 0 {
			delete(order, name)
		}
	}
	return marshal(t, order)
}

func TestRun_OUT02_TEST08_AGeneratedReplyCompletesOnlyIfTheReferenceValidatorAcceptsIt(t *testing.T) {
	t.Parallel()
	rapid.Check(t, func(t *rapid.T) {
		text := replyText(t)
		model := officinatest.NewModel("scripted", officinatest.TextReply(text))
		output, err := officina.NewOutput[orderOutput]()
		if err != nil {
			t.Fatalf("NewOutput() error = %v", err)
		}
		agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{Output: output})
		if err != nil {
			t.Fatalf("NewAgent() error = %v", err)
		}

		res, err := agent.Run(context.Background(), nil, "Read the order.", officina.RunOptions{})
		if err != nil {
			t.Fatalf("Run() error = %v", err)
		}

		var value any
		// A copy above 255 is valid against the schema but does not fit the type's uint8.
		fits := json.Unmarshal([]byte(text), &value) == nil &&
			referenceValid(t, string(model.Requests()[0].OutputSchema), text) && !strings.Contains(text, "300")
		switch {
		case fits && (res.Status != officina.Completed || res.Output == nil):
			t.Fatalf("reply %s: result %+v, want Completed with the output", text, res)
		case !fits && (res.Status != officina.Failed || res.Failure != officina.InvalidOutput || res.Output != nil):
			t.Fatalf("reply %s: result %+v, want Failed(InvalidOutput)", text, res)
		}
		if _, ok := res.Output.(orderOutput); fits && !ok {
			t.Fatalf("output %T, want orderOutput", res.Output)
		}
	})
}
