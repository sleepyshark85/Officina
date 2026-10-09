package officina_test

import (
	"bytes"
	"encoding/json/v2"
	"slices"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"
	"github.com/santhosh-tekuri/jsonschema/v6"
	"pgregory.net/rapid"

	"github.com/sleepyshark85/officina/go/officina"
)

func TestValidate_TOOL02_ChecksEachKeywordOfTheSubset(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name, schema, input string
		want                []string
	}{
		{"type", `{"type":"string"}`, `1`, []string{"/: must be string"}},
		{"types", `{"type":["string","null"]}`, `null`, nil},
		{"integer", `{"type":"integer"}`, `1.5`, []string{"/: must be integer"}},
		{"integral number", `{"type":"integer"}`, `2.0`, nil},
		{"enum", `{"enum":["a",1]}`, `"b"`, []string{`/: must be one of ["a",1]`}},
		{"enum by value", `{"enum":[1]}`, `1.0`, nil},
		{"const", `{"const":{"a":[1]}}`, `{"a":[2]}`, []string{`/: must be {"a":[1]}`}},
		{"anyOf", `{"anyOf":[{"type":"string"},{"minimum":3}]}`, `2`, []string{"/: matches none of the allowed schemas"}},
		{
			"properties and required", `{"properties":{"a":{"type":"string"}},"required":["a","b"]}`, `{"a":1}`,
			[]string{"/b: is required", "/a: must be string"},
		},
		{
			"additional properties", `{"properties":{"a":{}},"additionalProperties":false}`, `{"a":1,"z":2}`,
			[]string{"/z: is not allowed"},
		},
		{"additional schema", `{"additionalProperties":{"type":"integer"}}`, `{"z":"x"}`, []string{"/z: must be integer"}},
		{"items", `{"items":{"type":"integer"}}`, `[1,"x"]`, []string{"/1: must be integer"}},
		{"item counts", `{"minItems":2,"maxItems":1}`, `[1]`, []string{"/: must have at least 2 items"}},
		{"no item counts", `{"type":"array"}`, `[]`, nil},
		{"item counts met exactly", `{"minItems":2,"maxItems":2}`, `[1,2]`, nil},
		{"no items allowed", `{"maxItems":0}`, `[1]`, []string{"/: must have at most 0 items"}},
		{"no characters allowed", `{"maxLength":0}`, `"a"`, []string{"/: must have at most 0 characters"}},
		{"the largest count", `{"maxLength":2147483647}`, `"a"`, nil},
		{"bounds met exactly", `{"minimum":1,"maximum":1}`, `1`, nil},
		{"zero counts", `{"minItems":0,"maxItems":0,"minLength":0,"maxLength":0}`, `[]`, nil},
		{"lengths in characters", `{"minLength":2,"maxLength":2}`, `"éé"`, nil},
		{"too long", `{"maxLength":1}`, `"ab"`, []string{"/: must have at most 1 characters"}},
		{"too short", `{"minLength":1}`, `""`, []string{"/: must have at least 1 characters"}},
		{"bounds", `{"minimum":1,"maximum":2}`, `3`, []string{"/: must be at most 2"}},
		{"below", `{"minimum":1.5}`, `1`, []string{"/: must be at least 1.5"}},
		{"pattern", `{"pattern":"^[a-z]+$"}`, `"ab1"`, []string{"/: must match ^[a-z]+$"}},
		{"false", `false`, `1`, []string{"/: is not allowed"}},
		{"annotations", `{"title":"t","description":"d","default":1,"format":"email","examples":[]}`, `"x"`, nil},
		{"keywords of another type", `{"minLength":5,"minimum":5,"items":false}`, `{"a":1}`, nil},
		{"nested path", `{"properties":{"a":{"items":{"type":"string"}}}}`, `{"a":[1]}`, []string{"/a/0: must be string"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			got, err := officina.Validate(tt.schema, tt.input)
			if err != nil {
				t.Fatalf("Validate() error = %v", err)
			}

			if diff := cmp.Diff(tt.want, got); diff != "" {
				t.Errorf("problems mismatch (-want +got):\n%s", diff)
			}
		})
	}
}

func TestValidate_TEST08_RefusesASchemaOutsideTheSubset(t *testing.T) {
	t.Parallel()
	tests := []struct{ name, schema, want string }{
		{"unknown keyword", `{"oneOf":[{}]}`, `"/oneOf"`},
		{"reference", `{"properties":{"a":{"$ref":"#"}}}`, `"/properties/a/$ref"`},
		{"unknown type", `{"type":"date"}`, "unknown type"},
		{"no types", `{"type":[]}`, "unknown type"},
		{"not a schema", `{"items":1}`, "a schema must be an object or a boolean"},
		{"properties not an object", `{"properties":[]}`, "must be an object"},
		{"required not names", `{"required":[1]}`, "must be an array of names"},
		{"empty anyOf", `{"anyOf":[]}`, "must be a non-empty array"},
		{"enum not an array", `{"enum":1}`, "must be an array"},
		{"negative length", `{"minLength":-1}`, "must be a non-negative integer"},
		{"fractional count", `{"maxItems":1.5}`, "must be a non-negative integer"},
		{"bound not a number", `{"maximum":"1"}`, "must be a number"},
		{"pattern not a string", `{"pattern":1}`, "must be a string"},
		{"invalid pattern", `{"pattern":"("}`, "missing closing )"},
		{"anyOf option", `{"anyOf":[{"not":{}}]}`, `"/anyOf/0/not"`},
		{"not JSON", `{`, "read the schema"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()

			_, err := officina.Validate(tt.schema, `1`)

			if err == nil || !strings.Contains(err.Error(), tt.want) {
				t.Errorf("Validate() error = %v, want one containing %q", err, tt.want)
			}
		})
	}
}

// The generators draw from small sets of names, numbers and strings, so generated inputs often meet generated
// schemas, and both accepted and rejected cases are common.
func names() []string    { return []string{"a", "b", "c"} }
func numbers() []float64 { return []float64{-1, 0, 1, 1.5, 2, 3} }
func strs() []string     { return []string{"", "a", "ab", "b1", "é"} }
func patterns() []string { return []string{"^a", "b$", "[0-9]", "^$"} }
func jsonTypes() []string {
	return []string{"object", "array", "string", "number", "integer", "boolean", "null"}
}

func valueGen(depth int) *rapid.Generator[any] {
	return rapid.Custom(func(t *rapid.T) any {
		kinds := 5
		if depth > 0 {
			kinds = 7
		}
		switch rapid.IntRange(0, kinds-1).Draw(t, "kind") {
		case 0:
			return nil
		case 1:
			return rapid.Bool().Draw(t, "bool")
		case 2, 3:
			return rapid.SampledFrom(numbers()).Draw(t, "number")
		case 4:
			return rapid.SampledFrom(strs()).Draw(t, "string")
		case 5:
			return rapid.SliceOfN(valueGen(depth-1), 0, 3).Draw(t, "array")
		default:
			return rapid.MapOfN(rapid.SampledFrom(append(names(), "d")), valueGen(depth-1), 0, 3).Draw(t, "object")
		}
	})
}

func schemaGen(depth int) *rapid.Generator[any] {
	return rapid.Custom(func(t *rapid.T) any {
		if rapid.IntRange(0, 9).Draw(t, "boolean schema") == 0 {
			return rapid.Bool().Draw(t, "boolean")
		}
		s := map[string]any{}
		draw := func(keyword string) bool { return rapid.IntRange(0, 4).Draw(t, keyword) == 0 }
		if rapid.Bool().Draw(t, "type") {
			ts := rapid.SliceOfNDistinct(rapid.SampledFrom(jsonTypes()), 1, 2, rapid.ID).Draw(t, "types")
			s["type"] = any(ts[0])
			if len(ts) > 1 {
				s["type"] = ts
			}
		}
		if depth > 0 && draw("properties") {
			s["properties"] = rapid.MapOfN(rapid.SampledFrom(names()), schemaGen(depth-1), 1, 3).Draw(t, "props")
		}
		if draw("required") {
			s["required"] = rapid.SliceOfNDistinct(rapid.SampledFrom(names()), 1, 3, rapid.ID).Draw(t, "names")
		}
		if depth > 0 && draw("additionalProperties") {
			s["additionalProperties"] = schemaGen(depth-1).Draw(t, "additional")
		}
		if depth > 0 && draw("items") {
			s["items"] = schemaGen(depth-1).Draw(t, "item schema")
		}
		if depth > 0 && draw("anyOf") {
			s["anyOf"] = rapid.SliceOfN(schemaGen(depth-1), 1, 2).Draw(t, "options")
		}
		if draw("enum") {
			s["enum"] = rapid.SliceOfN(valueGen(1), 1, 3).Draw(t, "values")
		}
		if draw("const") {
			s["const"] = valueGen(1).Draw(t, "value")
		}
		for _, keyword := range []string{"minLength", "maxLength", "minItems", "maxItems"} {
			if draw(keyword) {
				s[keyword] = rapid.IntRange(0, 3).Draw(t, keyword+" value")
			}
		}
		for _, keyword := range []string{"minimum", "maximum"} {
			if draw(keyword) {
				s[keyword] = rapid.SampledFrom(numbers()).Draw(t, keyword+" value")
			}
		}
		if draw("pattern") {
			s["pattern"] = rapid.SampledFrom(patterns()).Draw(t, "regexp")
		}
		if draw("description") {
			s["description"] = "described"
		}
		return s
	})
}

func TestValidate_TEST08_AgreesWithAReferenceValidator(t *testing.T) {
	t.Parallel()
	rapid.Check(t, func(t *rapid.T) {
		schema := marshal(t, schemaGen(2).Draw(t, "schema"))
		input := marshal(t, valueGen(2).Draw(t, "input"))

		problems, err := officina.Validate(schema, input)
		if err != nil {
			t.Fatalf("Validate() error = %v", err)
		}
		reference := referenceValid(t, schema, input)

		if got := problems == nil; got != reference {
			t.Fatalf("Validate(%s, %s) accepts = %v, problems %q; the reference validator accepts = %v",
				schema, input, got, problems, reference)
		}
	})
}

func marshal(t *rapid.T, v any) string {
	data, err := json.Marshal(v, json.Deterministic(true))
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	return string(data)
}

// referenceValid reports whether santhosh-tekuri/jsonschema, under draft 2020-12, accepts input against schema.
func referenceValid(t *rapid.T, schema, input string) bool {
	doc, err := jsonschema.UnmarshalJSON(strings.NewReader(schema))
	if err != nil {
		t.Fatalf("reference: read the schema: %v", err)
	}
	c := jsonschema.NewCompiler()
	c.DefaultDraft(jsonschema.Draft2020)
	if err := c.AddResource("schema.json", doc); err != nil {
		t.Fatalf("reference: add the schema: %v", err)
	}
	compiled, err := c.Compile("schema.json")
	if err != nil {
		t.Fatalf("reference: compile %s: %v", schema, err)
	}
	value, err := jsonschema.UnmarshalJSON(strings.NewReader(input))
	if err != nil {
		t.Fatalf("reference: read the input: %v", err)
	}
	return compiled.Validate(value) == nil
}

func FuzzValidate_TOOL02_NeverPanicsAndIsDeterministic(f *testing.F) {
	f.Add(`{"type":"object","properties":{"a":{"type":"string","pattern":"^x"}},"required":["a"]}`, `{"a":"xy"}`)
	f.Add(`{"anyOf":[{"enum":[1,"a"]},{"items":{"const":null}}],"additionalProperties":false}`, `[null,{}]`)
	f.Add(`{"minLength":1,"maxItems":0,"minimum":-1e308}`, `"é"`)
	f.Add(`true`, `1e400`)
	f.Fuzz(func(t *testing.T, schema, input string) {
		first, err := officina.Validate(schema, input)
		if err != nil {
			return
		}
		second, _ := officina.Validate(schema, input) // The same call succeeded just above.
		if !slices.Equal(first, second) {
			t.Errorf("Validate() = %q, then %q", first, second)
		}
		for _, p := range first {
			if !strings.HasPrefix(p, "/") || !bytes.Contains([]byte(p), []byte(": ")) {
				t.Errorf("problem %q is not of the form <path>: <problem>", p)
			}
		}
	})
}
