package officina

import (
	"context"
	"encoding"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"reflect"
	"slices"
	"strconv"
	"strings"
	"time"
)

// Tool is an action the model may request. Its name, description and input schema reach the model, so they are part
// of the prefix; its kind, approval need and handler do not.
type Tool struct {
	// Name is unique among an agent's tools.
	Name        string
	Description string
	// InputSchema is the JSON Schema of the tool's input: an object schema in the subset the core validates, sent
	// to the model as written.
	InputSchema jsontext.Value
	Kind        ToolKind
	// NeedsApproval says the agent's approver must approve each call before it runs.
	NeedsApproval bool
	// Handler runs the tool on input that is valid against InputSchema, and returns the result for the model. An
	// error, or a panic, is the call's error result, with the error's text; the run goes on.
	Handler func(ctx context.Context, input jsontext.Value) (string, error)
	// Source is the source the tool comes from, such as an MCP server; nil for the application's own tools.
	Source ToolSource
}

// ToolKind says whether a tool only reads or changes something.
type ToolKind int

// The kinds of tool; the zero value is neither, so every tool declares its kind.
const (
	// Read is a tool that only reads: the read calls of one reply run concurrently.
	Read ToolKind = iota + 1
	// Write is a tool that changes something: write calls run one at a time, in order, each audited before it runs.
	Write
)

// String returns the kind's name.
func (k ToolKind) String() string {
	return name(int(k), "ToolKind", "Read", "Write")
}

// Approver answers approval requests: a person or a policy. An agent without one runs unattended, and calls that
// need approval are denied.
type Approver interface {
	// Approve decides whether call, of tool t, may run. Calls are asked about one at a time. An error denies the
	// call, and the model is told why.
	Approve(ctx context.Context, t Tool, call ToolCall) (Approval, error)
}

// Approval is an approver's answer; a denial's reason is told to the model.
type Approval struct {
	Approved bool
	Reason   string
}

// NewTool returns a tool that runs fn. Its input schema is derived from In, which must be a struct: each exported
// field is a property named as encoding/json/v2 names it, required unless its json tag says omitempty or omitzero,
// and described by its jsonschema tag. A string fn returns is the result as it is; any other value is sent as JSON.
// It fails for a field whose type has no schema in the supported subset, such as an interface, a func, a
// []byte, an embedded struct or a type with its own JSON form (time.Time is a date-time string).
func NewTool[In, Out any](name, description string, kind ToolKind, fn func(ctx context.Context, in In) (Out, error),
) (Tool, error) {
	t := reflect.TypeFor[In]()
	if t.Kind() != reflect.Struct {
		return Tool{}, fmt.Errorf("new tool %q: the input is a %s, not a struct", name, t)
	}
	n, err := describe(t, nil)
	if err != nil {
		return Tool{}, fmt.Errorf("new tool %q: %w", name, err)
	}
	handler := func(ctx context.Context, input jsontext.Value) (string, error) {
		var in In
		if err := json.Unmarshal(input, &in); err != nil {
			return "", fmt.Errorf("read the input: %w", err)
		}
		out, err := fn(ctx, in)
		if err != nil {
			return "", err
		}
		if text, ok := any(out).(string); ok {
			return text, nil
		}
		data, err := json.Marshal(out)
		if err != nil {
			return "", fmt.Errorf("write the result: %w", err)
		}
		return string(data), nil
	}
	return Tool{Name: name, Description: description, InputSchema: n.json(), Kind: kind, Handler: handler}, nil
}

// node is a schema derived from a Go type, written in a fixed key order.
type node struct {
	types               []string
	description, format string
	nonNegative         bool
	items, values       *node
	minItems            int
	props               []property
	closed              bool
}

type property struct {
	name     string
	schema   *node
	required bool
}

// describe returns the schema of t; within is the types it is nested in, to refuse a recursive one.
func describe(t reflect.Type, within []reflect.Type) (*node, error) {
	if slices.Contains(within, t) {
		return nil, fmt.Errorf("%s contains itself", t)
	}
	within = append(within, t)
	switch {
	case t == reflect.TypeFor[time.Time]():
		return &node{types: []string{"string"}, format: "date-time"}, nil
	case t == reflect.TypeFor[time.Duration]() || ownJSON(t):
		return nil, fmt.Errorf("%s has a JSON form of its own, which has no schema", t)
	}
	switch t.Kind() {
	case reflect.Bool:
		return &node{types: []string{"boolean"}}, nil
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		return &node{types: []string{"integer"}}, nil
	case reflect.Uint, reflect.Uint8, reflect.Uint16, reflect.Uint32, reflect.Uint64:
		return &node{types: []string{"integer"}, nonNegative: true}, nil
	case reflect.Float32, reflect.Float64:
		return &node{types: []string{"number"}}, nil
	case reflect.String:
		return &node{types: []string{"string"}}, nil
	case reflect.Pointer:
		n, err := describe(t.Elem(), within)
		if err == nil && !slices.Contains(n.types, "null") {
			n.types = append(n.types, "null")
		}
		return n, err
	case reflect.Slice, reflect.Array:
		if t.Elem().Kind() == reflect.Uint8 {
			return nil, fmt.Errorf("%s is bytes, which have no schema", t)
		}
		items, err := describe(t.Elem(), within)
		n := &node{types: []string{"array"}, items: items, minItems: -1}
		if t.Kind() == reflect.Array {
			n.minItems = t.Len()
		}
		return n, err
	case reflect.Map:
		if t.Key().Kind() != reflect.String {
			return nil, fmt.Errorf("%s has keys that are not strings", t)
		}
		values, err := describe(t.Elem(), within)
		return &node{types: []string{"object"}, values: values}, err
	case reflect.Struct:
		return describeStruct(t, within)
	default:
		return nil, fmt.Errorf("%s has no schema", t)
	}
}

func describeStruct(t reflect.Type, within []reflect.Type) (*node, error) {
	n := &node{types: []string{"object"}, closed: true}
	for i := range t.NumField() {
		field := t.Field(i)
		if field.Anonymous {
			return nil, fmt.Errorf("%s embeds %s, which is not supported", t, field.Type)
		}
		tag, has := field.Tag.Lookup("json")
		if !field.IsExported() || tag == "-" {
			continue
		}
		name, options, _ := strings.Cut(tag, ",")
		if !has || name == "" {
			name = field.Name
		}
		optional := false
		for option := range strings.SplitSeq(options, ",") {
			switch {
			case option == "omitempty" || option == "omitzero":
				optional = true
			case option != "" && option != "case:ignore" && option != "case:strict":
				return nil, fmt.Errorf("field %s of %s: the json option %q is not supported", field.Name, t, option)
			}
		}
		schema, err := describe(field.Type, within)
		if err != nil {
			return nil, fmt.Errorf("field %s of %s: %w", field.Name, t, err)
		}
		schema.description = field.Tag.Get("jsonschema")
		n.props = append(n.props, property{name: name, schema: schema, required: !optional})
	}
	return n, nil
}

// ownJSON reports whether t, or a pointer to it, marshals itself.
func ownJSON(t reflect.Type) bool {
	for _, own := range []reflect.Type{
		reflect.TypeFor[json.Marshaler](), reflect.TypeFor[json.MarshalerTo](), reflect.TypeFor[encoding.TextMarshaler](),
		reflect.TypeFor[encoding.TextAppender](),
	} {
		if t.Implements(own) || reflect.PointerTo(t).Implements(own) {
			return true
		}
	}
	return false
}

// json returns the schema as compact JSON.
func (n *node) json() jsontext.Value {
	return n.append(nil)
}

func (n *node) append(b []byte) []byte {
	b = append(b, `{"type":`...)
	if len(n.types) == 1 {
		b = quote(b, n.types[0])
	} else {
		b = append(b, '[')
		for i, t := range n.types {
			if i > 0 {
				b = append(b, ',')
			}
			b = quote(b, t)
		}
		b = append(b, ']')
	}
	if n.description != "" {
		b = quote(append(b, `,"description":`...), n.description)
	}
	if n.format != "" {
		b = quote(append(b, `,"format":`...), n.format)
	}
	if n.nonNegative {
		b = append(b, `,"minimum":0`...)
	}
	if n.items != nil {
		b = n.items.append(append(b, `,"items":`...))
	}
	if n.minItems >= 0 && n.items != nil {
		b = strconv.AppendInt(append(b, `,"minItems":`...), int64(n.minItems), 10)
		b = strconv.AppendInt(append(b, `,"maxItems":`...), int64(n.minItems), 10)
	}
	if n.values != nil {
		b = n.values.append(append(b, `,"additionalProperties":`...))
	}
	if n.closed {
		b = append(b, `,"properties":{`...)
		var required []byte
		for i, p := range n.props {
			if i > 0 {
				b = append(b, ',')
			}
			b = p.schema.append(append(quote(b, p.name), ':'))
			if p.required {
				if required != nil {
					required = append(required, ',')
				}
				required = quote(required, p.name)
			}
		}
		b = append(append(append(b, `},"required":[`...), required...), `],"additionalProperties":false`...)
	}
	return append(b, '}')
}

// quote appends s as a JSON string.
func quote(b []byte, s string) []byte {
	b, err := jsontext.AppendQuote(b, s)
	if err != nil {
		// Only invalid UTF-8 fails, and Go names and tags in source are valid UTF-8; a type built at run time is
		// a bug in its caller.
		panic(errors.New("officina: invalid UTF-8 in a type's name or tag")) //nolint:forbidigo // A bug, see above.
	}
	return b
}
