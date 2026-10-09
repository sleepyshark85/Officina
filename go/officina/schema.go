package officina

import (
	"cmp"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"maps"
	"math"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"unicode/utf8"
)

// schema is a JSON Schema (draft 2020-12) compiled for validation, in the subset the core supports: what it derives
// from Go types, and what MCP servers commonly send. A schema outside the subset is refused when it is compiled, so
// none is half checked. Numbers are compared as float64, and a pattern is a Go regular expression.
type schema struct {
	// boolean is the schema true or false, when the schema is one.
	boolean  *bool
	types    []string
	props    map[string]*schema
	required []string
	// additional is additionalProperties; items is items.
	additional, items *schema
	anyOf             []*schema
	enum              []any
	hasEnum           bool
	constant          any
	hasConst          bool
	// The bounds, each -1 when absent.
	minLength, maxLength, minItems, maxItems int
	minimum, maximum                         *float64
	pattern                                  *regexp.Regexp
}

// annotation reports whether keyword describes a value without constraining it.
func annotation(keyword string) bool {
	return slices.Contains([]string{"$schema", "$id", "$comment", "title", "description", "default", "examples",
		"format", "readOnly", "writeOnly", "deprecated"}, keyword)
}

// compileSchema compiles raw, or says where it leaves the supported subset.
func compileSchema(raw jsontext.Value) (*schema, error) {
	var v any
	if err := json.Unmarshal(raw, &v); err != nil {
		return nil, fmt.Errorf("read the schema: %w", err)
	}
	return compile(v, "")
}

func compile(v any, path string) (*schema, error) {
	if b, ok := v.(bool); ok {
		return &schema{boolean: &b}, nil
	}
	object, ok := v.(map[string]any)
	if !ok {
		return nil, outside(path, "a schema must be an object or a boolean")
	}
	s := &schema{minLength: -1, maxLength: -1, minItems: -1, maxItems: -1}
	for _, keyword := range slices.Sorted(maps.Keys(object)) {
		value, at := object[keyword], path+"/"+keyword
		var err error
		switch keyword {
		case "type":
			s.types, err = typeNames(value, at)
		case "properties":
			s.props, err = compileProperties(value, at)
		case "required":
			s.required, err = names(value, at)
		case "additionalProperties":
			s.additional, err = compile(value, at)
		case "items":
			s.items, err = compile(value, at)
		case "anyOf":
			s.anyOf, err = compileAnyOf(value, at)
		case "enum":
			s.enum, s.hasEnum = value.([]any)
			if !s.hasEnum {
				err = outside(at, "must be an array")
			}
		case "const":
			s.constant, s.hasConst = value, true
		case "minLength", "maxLength", "minItems", "maxItems":
			n, ok := value.(float64)
			if !ok || n < 0 || n != math.Trunc(n) || n > math.MaxInt32 {
				return nil, outside(at, "must be a non-negative integer")
			}
			switch keyword {
			case "minLength":
				s.minLength = int(n)
			case "maxLength":
				s.maxLength = int(n)
			case "minItems":
				s.minItems = int(n)
			default:
				s.maxItems = int(n)
			}
		case "minimum", "maximum":
			n, ok := value.(float64)
			if !ok {
				return nil, outside(at, "must be a number")
			}
			if keyword == "minimum" {
				s.minimum = &n
			} else {
				s.maximum = &n
			}
		case "pattern":
			text, ok := value.(string)
			if !ok {
				return nil, outside(at, "must be a string")
			}
			if s.pattern, err = regexp.Compile(text); err != nil {
				err = outside(at, err.Error())
			}
		default:
			if !annotation(keyword) {
				err = outside(at, "is not a supported keyword")
			}
		}
		if err != nil {
			return nil, err
		}
	}
	return s, nil
}

func typeNames(v any, at string) ([]string, error) {
	list, ok := v.([]any)
	if !ok {
		list = []any{v}
	}
	types := make([]string, len(list))
	for i, item := range list {
		name, ok := item.(string)
		if !ok || !slices.Contains([]string{"object", "array", "string", "number", "integer", "boolean", "null"}, name) {
			return nil, outside(at, "unknown type")
		}
		types[i] = name
	}
	if len(types) == 0 {
		return nil, outside(at, "unknown type")
	}
	return types, nil
}

func compileProperties(v any, at string) (map[string]*schema, error) {
	object, ok := v.(map[string]any)
	if !ok {
		return nil, outside(at, "must be an object")
	}
	props := make(map[string]*schema, len(object))
	for name, value := range object {
		p, err := compile(value, at+"/"+name)
		if err != nil {
			return nil, err
		}
		props[name] = p
	}
	return props, nil
}

func names(v any, at string) ([]string, error) {
	list, ok := v.([]any)
	if !ok {
		return nil, outside(at, "must be an array of names")
	}
	result := make([]string, len(list))
	for i, item := range list {
		if result[i], ok = item.(string); !ok {
			return nil, outside(at, "must be an array of names")
		}
	}
	return result, nil
}

func compileAnyOf(v any, at string) ([]*schema, error) {
	list, ok := v.([]any)
	if !ok || len(list) == 0 {
		return nil, outside(at, "must be a non-empty array")
	}
	options := make([]*schema, len(list))
	for i, item := range list {
		option, err := compile(item, at+"/"+strconv.Itoa(i))
		if err != nil {
			return nil, err
		}
		options[i] = option
	}
	return options, nil
}

func outside(path, problem string) error {
	return fmt.Errorf("the schema at %q is outside the supported subset: %s", cmp.Or(path, "/"), problem)
}

// validate returns the problems of value, a JSON value as json.Unmarshal reads it into an any, one per line as
// "<path>: <problem>"; none when it is valid.
func (s *schema) validate(value any) []string {
	var problems []string
	s.check(value, "", &problems)
	return problems
}

func (s *schema) check(value any, path string, problems *[]string) {
	fail := func(problem string) {
		*problems = append(*problems, cmp.Or(path, "/")+": "+problem)
	}
	if s.boolean != nil {
		if !*s.boolean {
			fail("is not allowed")
		}
		return
	}
	if s.types != nil && !slices.ContainsFunc(s.types, func(t string) bool { return isType(value, t) }) {
		fail("must be " + strings.Join(s.types, " or "))
	}
	if s.hasEnum && !slices.ContainsFunc(s.enum, func(option any) bool { return equal(option, value) }) {
		fail("must be one of " + show(s.enum))
	}
	if s.hasConst && !equal(s.constant, value) {
		fail("must be " + show(s.constant))
	}
	if s.anyOf != nil && !slices.ContainsFunc(s.anyOf, func(option *schema) bool { return option.validate(value) == nil }) {
		fail("matches none of the allowed schemas")
	}
	switch v := value.(type) {
	case map[string]any:
		s.checkObject(v, path, problems)
	case []any:
		if s.minItems >= 0 && len(v) < s.minItems {
			fail(fmt.Sprintf("must have at least %d items", s.minItems))
		}
		if s.maxItems >= 0 && len(v) > s.maxItems {
			fail(fmt.Sprintf("must have at most %d items", s.maxItems))
		}
		for i, item := range v {
			if s.items != nil {
				s.items.check(item, path+"/"+strconv.Itoa(i), problems)
			}
		}
	case string:
		n := utf8.RuneCountInString(v)
		if s.minLength >= 0 && n < s.minLength {
			fail(fmt.Sprintf("must have at least %d characters", s.minLength))
		}
		if s.maxLength >= 0 && n > s.maxLength {
			fail(fmt.Sprintf("must have at most %d characters", s.maxLength))
		}
		if s.pattern != nil && !s.pattern.MatchString(v) {
			fail("must match " + s.pattern.String())
		}
	case float64:
		if s.minimum != nil && v < *s.minimum {
			fail("must be at least " + show(*s.minimum))
		}
		if s.maximum != nil && v > *s.maximum {
			fail("must be at most " + show(*s.maximum))
		}
	}
}

func (s *schema) checkObject(object map[string]any, path string, problems *[]string) {
	for _, name := range s.required {
		if _, ok := object[name]; !ok {
			*problems = append(*problems, path+"/"+name+": is required")
		}
	}
	for _, name := range slices.Sorted(maps.Keys(object)) {
		if p, ok := s.props[name]; ok {
			p.check(object[name], path+"/"+name, problems)
		} else if s.additional != nil {
			s.additional.check(object[name], path+"/"+name, problems)
		}
	}
}

func isType(value any, t string) bool {
	switch v := value.(type) {
	case map[string]any:
		return t == "object"
	case []any:
		return t == "array"
	case string:
		return t == "string"
	case bool:
		return t == "boolean"
	case float64:
		return t == "number" || t == "integer" && v == math.Trunc(v)
	default:
		return t == "null"
	}
}

// equal reports whether two JSON values are equal as JSON Schema compares them: numbers by value, objects whatever
// their order.
func equal(a, b any) bool {
	switch x := a.(type) {
	case map[string]any:
		y, ok := b.(map[string]any)
		return ok && maps.EqualFunc(x, y, equal)
	case []any:
		y, ok := b.([]any)
		return ok && slices.EqualFunc(x, y, equal)
	default:
		return a == b
	}
}

// show returns v as JSON, for a problem's message.
func show(v any) string {
	data, err := json.Marshal(v, json.Deterministic(true))
	if err != nil {
		return fmt.Sprint(v)
	}
	return string(data)
}
