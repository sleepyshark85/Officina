package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

type address struct {
	City string `json:"city"`
}

type order struct {
	ISBN    string            `json:"isbn" jsonschema:"The book's ISBN."`
	Copies  uint8             `json:"copies,omitempty"`
	Price   float64           `json:"price"`
	Gift    bool              `json:"gift,omitzero"`
	Note    *string           `json:"note"`
	Tags    []string          `json:"tags"`
	Pair    [2]int            `json:"pair"`
	None    [0]int            `json:"none"`
	Extra   map[string]int64  `json:"extra"`
	Ship    address           `json:"ship"`
	Due     time.Time         `json:"due"`
	Exact   int               `json:",case:strict"`
	Skipped string            `json:"-"`
	hidden  string            //nolint:unused // An unexported field is not part of the schema.
	Nested  []map[string]bool `json:"nested,omitempty"`
	Cover   binding           `json:"cover" enum:"paperback,hardback"`
}

// binding is a string type whose values the cover field's enum tag lists.
type binding string

func TestNewTool_TOOL01_DerivesTheSchemaFromTheInputType(t *testing.T) {
	t.Parallel()

	got, err := officina.NewTool("order", "Orders a book.", officina.Write,
		func(context.Context, order) (string, error) { return "", nil })
	if err != nil {
		t.Fatalf("NewTool() error = %v", err)
	}

	want := `{"type":"object","properties":{` +
		`"isbn":{"description":"The book's ISBN.","type":"string"},` +
		`"copies":{"type":"integer","minimum":0},` +
		`"price":{"type":"number"},` +
		`"gift":{"type":"boolean"},` +
		`"note":{"type":["string","null"]},` +
		`"tags":{"type":"array","items":{"type":"string"}},` +
		`"pair":{"type":"array","items":{"type":"integer"},"minItems":2,"maxItems":2},` +
		`"none":{"type":"array","items":{"type":"integer"},"minItems":0,"maxItems":0},` +
		`"extra":{"type":"object","additionalProperties":{"type":"integer"}},` +
		`"ship":{"type":"object","properties":{"city":{"type":"string"}},"required":["city"],"additionalProperties":false},` +
		`"due":{"type":"string","format":"date-time"},` +
		`"Exact":{"type":"integer"},` +
		`"nested":{"type":"array","items":{"type":"object","additionalProperties":{"type":"boolean"}}},` +
		`"cover":{"type":"string","enum":["paperback","hardback"]}},` +
		`"required":["isbn","price","note","tags","pair","none","extra","ship","due","Exact","cover"],"additionalProperties":false}`
	if diff := cmp.Diff(want, string(got.InputSchema)); diff != "" {
		t.Errorf("schema mismatch (-want +got):\n%s", diff)
	}
	if got.Name != "order" || got.Description != "Orders a book." || got.Kind != officina.Write || got.NeedsApproval {
		t.Errorf("tool = %+v, want the name, description and kind given, without approval", got)
	}
	if _, err := officina.NewAgent(officinatest.NewModel("scripted"), instructions,
		officina.AgentOptions{Tools: []officina.Tool{got}}); err != nil {
		t.Errorf("NewAgent() with the derived schema error = %v", err)
	}
}

func TestNewTool_TOOL01_RefusesATypeWithoutASchema(t *testing.T) {
	t.Parallel()
	type (
		recursive struct {
			Next *recursive
		}
		embedded struct {
			address
		}
	)
	tests := []struct {
		name string
		make func() (officina.Tool, error)
		want string
	}{
		{"not a struct", newTool[string], "the input is a string, not a struct"},
		{"interface", newTool[struct{ V any }], "field V of struct { V interface {} }: interface {} has no schema"},
		{"func", newTool[struct{ F func() }], "func() has no schema"},
		{"bytes", newTool[struct{ B []byte }], "[]uint8 is bytes, which have no schema"},
		{"duration", newTool[struct{ D time.Duration }], "time.Duration has a JSON form of its own"},
		{"own JSON form", newTool[struct{ R jsontext.Value }], "jsontext.Value has a JSON form of its own"},
		{"map of int keys", newTool[struct{ M map[int]string }], "map[int]string has keys that are not strings"},
		{"recursive", newTool[recursive], "officina_test.recursive contains itself"},
		{"embedded", newTool[embedded], "embeds officina_test.address, which is not supported"},
		{"string option", newTool[struct {
			N int `json:"n,string"`
		}], `the json option "string" is not supported`},
		{"enum of numbers", newTool[struct {
			N int `enum:"1,2"`
		}], "an enum tag needs a string field"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			_, err := tt.make()

			if err == nil || !strings.Contains(err.Error(), tt.want) {
				t.Errorf("NewTool() error = %v, want one containing %q", err, tt.want)
			}
		})
	}
}

// newTool makes a read tool of input In that does nothing.
func newTool[In any]() (officina.Tool, error) {
	return officina.NewTool("t", "", officina.Read, func(context.Context, In) (string, error) { return "", nil })
}

func TestNewTool_TOOL01_RunsOnTheDecodedInputAndSendsItsResultAsJSON(t *testing.T) {
	t.Parallel()
	type (
		query struct {
			Title string `json:"title"`
			Limit int    `json:"limit,omitempty"`
		}
		book struct {
			Title string `json:"title"`
			Stock int    `json:"stock"`
		}
	)
	search, err := officina.NewTool("search", "Searches.", officina.Read, func(_ context.Context, q query) ([]book, error) {
		if q.Title == "" {
			return nil, errors.New("no title")
		}
		return []book{{Title: q.Title + " <1>", Stock: q.Limit}}, nil
	})
	if err != nil {
		t.Fatalf("NewTool() error = %v", err)
	}
	echo, err := officina.NewTool("echo", "Echoes.", officina.Read, func(_ context.Context, q query) (string, error) {
		return q.Title, nil
	})
	if err != nil {
		t.Fatalf("NewTool() error = %v", err)
	}
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(
		officinatest.ToolUseBlock("c1", "search", `{"title":"Emma","limit":3}`),
		officinatest.ToolUseBlock("c2", "search", `{"title":""}`),
		officinatest.ToolUseBlock("c3", "echo", `{"title":"as is"}`),
	), officinatest.TextReply("Done."))
	var c officina.Conversation

	run(t, newAgent(t, model, search, echo), &c, "Find Emma", officina.RunOptions{})

	want := []officina.ToolResult{
		{CallID: "c1", Content: `[{"title":"Emma <1>","stock":3}]`},
		{CallID: "c2", Content: "no title", IsError: true},
		{CallID: "c3", Content: "as is"},
	}
	if diff := cmp.Diff(want, results(c.Messages()[2])); diff != "" {
		t.Errorf("results mismatch (-want +got):\n%s", diff)
	}
}

func TestNewAgent_TEST08_RefusesAToolOutsideTheSubset(t *testing.T) {
	t.Parallel()
	valid := tool("search", "Searches.")
	tests := []struct {
		name string
		edit func(*officina.Tool)
		want string
	}{
		{
			"keyword outside the subset",
			func(t *officina.Tool) { t.InputSchema = jsontext.Value(`{"type":"object","oneOf":[]}`) },
			`new agent: tool "search": the schema at "/oneOf" is outside the supported subset: is not a supported keyword`,
		},
		{
			"not an object schema", func(t *officina.Tool) { t.InputSchema = jsontext.Value(`{"type":"string"}`) },
			`new agent: tool "search": the input schema's type is not "object"`,
		},
		{
			"no type", func(t *officina.Tool) { t.InputSchema = jsontext.Value(`{}`) },
			`new agent: tool "search": the input schema's type is not "object"`,
		},
		{"no kind", func(t *officina.Tool) { t.Kind = 0 }, `new agent: tool "search": kind is neither Read nor Write`},
		{"no handler", func(t *officina.Tool) { t.Handler = nil }, `new agent: tool "search" has no handler`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			invalid := valid
			tt.edit(&invalid)

			_, err := officina.NewAgent(officinatest.NewModel("scripted"), instructions,
				officina.AgentOptions{Tools: []officina.Tool{invalid}})

			if err == nil || err.Error() != tt.want {
				t.Errorf("NewAgent() error = %v, want %q", err, tt.want)
			}
		})
	}
}
