package claude_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"slices"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

// written is a hand-written schema with every keyword the adjustment changes, and a property named like one of
// them: the .NET implementation's, whose request the shared output-written.json holds.
const written = `{"type":"object","properties":{
  "count":{"type":"integer","minimum":1,"maximum":9,"exclusiveMinimum":0,"exclusiveMaximum":10,"multipleOf":1},
  "maximum":{"type":"number"},
  "tags":{"type":"array","items":{"type":"string","pattern":"^[a-z]+$","minLength":1},"minItems":1,"maxItems":3},
  "pairs":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}}},"minItems":2},
  "note":{"anyOf":[{"type":"null"},{"type":["object","null"],"properties":{"text":{"type":"string"}}}]}},
 "required":["count"]}`

// sentOutput sends req, with the output schema given, through a Claude model at low effort, and returns the
// request's body.
func sentOutput(t *testing.T, schema string) map[string]any {
	t.Helper()
	api := serve(t, sse(textReply("end_turn")))
	m, err := claude.New(claude.Opus55, claude.EffortLow, api.options(claude.Options{}))
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	req := hi()
	req.OutputSchema = jsontext.Value(schema)
	if _, err := collect(t.Context(), m, req); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}
	var body map[string]any
	if err := json.Unmarshal([]byte(api.Requests()[0]), &body); err != nil {
		t.Fatalf("unmarshal the request: %v", err)
	}
	return body
}

// golden returns a shared fixture as parsed JSON.
func golden(t *testing.T, name string) map[string]any {
	t.Helper()
	var v map[string]any
	if err := json.Unmarshal([]byte(fixture(t, name)), &v); err != nil {
		t.Fatalf("unmarshal %s: %v", name, err)
	}
	return v
}

func TestModel_OUT01_AWrittenSchemaIsClosedAndStrippedOfWhatTheAPIRejectsAsDotNetSendsIt(t *testing.T) {
	t.Parallel()

	body := sentOutput(t, written)

	if diff := cmp.Diff(golden(t, "claude/output-written.json"), body["output_config"]); diff != "" {
		t.Errorf("output_config mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_OUT01_DotNetsExportedSchemaGoesOutAsDotNetSendsItAndToolChoiceIsNeverForced(t *testing.T) {
	t.Parallel()
	exported := golden(t, "claude/output-exported.json")
	schema, err := json.Marshal(exported["format"].(map[string]any)["schema"])
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}

	body := sentOutput(t, string(schema))

	if diff := cmp.Diff(exported, body["output_config"]); diff != "" {
		t.Errorf("output_config mismatch (-want +got):\n%s", diff)
	}
	if choice, ok := body["tool_choice"]; ok {
		t.Errorf("tool_choice = %v, want none", choice)
	}
}

type line struct {
	BookID int `json:"bookId" jsonschema:"The book's id."`
	Copies int `json:"copies"`
}

// order is the .NET test's Order but its mood, an enum, which Go has no type for.
type order struct {
	Customer string  `json:"customer"`
	Lines    []line  `json:"lines"`
	Gift     *line   `json:"gift"`
	Total    float64 `json:"total"`
}

func TestModel_OUT01_GEN05_ATypesSchemaGoesOutAsDotNetsAndTheStructuredReplyComesBackAsTheTypedOutput(t *testing.T) {
	t.Parallel()
	reply := `{"customer":"Ana","lines":[{"bookId":144,"copies":2}],"gift":null,"total":12.56}`
	api := serve(t, sse(textReply("end_turn", reply[:20], reply[20:])))
	m, err := claude.New(claude.Opus55, claude.EffortLow, api.options(claude.Options{}))
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	output, err := officina.NewOutput[order]()
	if err != nil {
		t.Fatalf("NewOutput() error = %v", err)
	}
	a, err := officina.NewAgent(m, "Read the order.", officina.AgentOptions{Output: output})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	res, err := a.Run(t.Context(), nil, "Ana wants two copies of book 144.", officina.RunOptions{})
	if err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	if res.Status != officina.Completed {
		t.Fatalf("result = %+v, want Completed", res)
	}
	want := order{Customer: "Ana", Lines: []line{{BookID: 144, Copies: 2}}, Total: 12.56}
	if diff := cmp.Diff(any(want), res.Output); diff != "" {
		t.Errorf("output mismatch (-want +got):\n%s", diff)
	}
	var body map[string]any
	if err := json.Unmarshal([]byte(api.Requests()[0]), &body); err != nil {
		t.Fatalf("unmarshal the request: %v", err)
	}
	// The same schema as .NET's for its Order, but the mood.
	exported := golden(t, "claude/output-exported.json")
	schema := exported["format"].(map[string]any)["schema"].(map[string]any)
	delete(schema["properties"].(map[string]any), "mood")
	schema["required"] = slices.DeleteFunc(schema["required"].([]any), func(name any) bool { return name == "mood" })
	if diff := cmp.Diff(exported, body["output_config"]); diff != "" {
		t.Errorf("output_config mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_OUT01_AnOpenObjectIsRefusedRatherThanClosed(t *testing.T) {
	t.Parallel()
	tests := []struct{ name, schema string }{
		{"a map", `{"type":"object","properties":{"counts":{"type":"object","additionalProperties":{"type":"integer"}}},` +
			`"additionalProperties":false}`},
		{"open at the root", `{"type":"object","additionalProperties":true}`},
		{"in anyOf", `{"anyOf":[{"type":"null"},{"type":"object","additionalProperties":{}}]}`},
		{"not JSON", `{"type":`},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			api := serve(t)
			req := hi()
			req.OutputSchema = jsontext.Value(tt.schema)

			_, err := collect(t.Context(), model(t, api, claude.Options{}), req)

			if err == nil || !strings.HasPrefix(err.Error(), "claude request: ") {
				t.Errorf("Stream() error = %v, want the schema refused", err)
			}
			if n := len(api.Requests()); n != 0 {
				t.Errorf("%d requests sent, want none", n)
			}
		})
	}
}

// walkSchemas calls visit on schema and every schema inside it, at the keywords the adjustment reads.
func walkSchemas(schema any, visit func(map[string]any)) {
	s, ok := schema.(map[string]any)
	if !ok {
		return
	}
	visit(s)
	if props, ok := s["properties"].(map[string]any); ok {
		for _, p := range props {
			walkSchemas(p, visit)
		}
	}
	walkSchemas(s["items"], visit)
	if options, ok := s["anyOf"].([]any); ok {
		for _, o := range options {
			walkSchemas(o, visit)
		}
	}
}

func FuzzOutputSchema_OUT01_ClosesEveryObjectDropsWhatTheAPIRejectsAndIsIdempotent(f *testing.F) {
	f.Add(written)
	f.Add(`{"type":["object","null"],"properties":{"a":{"type":"array","items":{"minItems":2,"maxItems":1}}}}`)
	f.Add(`{"anyOf":[true,{"type":"object","additionalProperties":false,"minimum":1}],"enum":[null]}`)
	f.Add(`{"properties":{"minimum":{"type":"object","properties":[]}},"anyOf":{"x":1}}`)
	f.Add(`{"type":"string","minItems":"2","items":false}`)
	f.Fuzz(func(t *testing.T, schema string) {
		adjusted, err := claude.OutputSchema(jsontext.Value(schema))
		if err != nil {
			if jsontext.Value(schema).IsValid() && !errors.Is(err, claude.ErrOpenObject) {
				t.Fatalf("OutputSchema(%s) error = %v, want only an open object refused", schema, err)
			}
			return
		}
		if !adjusted.IsValid() {
			t.Fatalf("OutputSchema(%s) = %s, not valid JSON", schema, adjusted)
		}
		again, err := claude.OutputSchema(adjusted)
		if err != nil || string(again) != string(adjusted) {
			t.Fatalf("OutputSchema(%s) = %s, %v; want it unchanged", adjusted, again, err)
		}
		var parsed any
		if json.Unmarshal(adjusted, &parsed) != nil {
			return // A number beyond float64's range: valid JSON, but not one any can hold.
		}
		walkSchemas(parsed, func(s map[string]any) {
			for _, keyword := range []string{"minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
				"multipleOf", "maxItems"} {
				if _, ok := s[keyword]; ok {
					t.Errorf("OutputSchema(%s) keeps %s in %v", schema, keyword, s)
				}
			}
			if n, ok := s["minItems"].(float64); ok && n > 1 {
				t.Errorf("OutputSchema(%s) keeps minItems %v", schema, n)
			}
			_, typed := s["properties"]
			switch types := s["type"].(type) {
			case string:
				typed = typed || types == "object"
			case []any:
				typed = typed || slices.Contains(types, any("object"))
			}
			if typed && s["additionalProperties"] != false {
				t.Errorf("OutputSchema(%s) leaves an object open: %v", schema, s)
			}
		})
	})
}
