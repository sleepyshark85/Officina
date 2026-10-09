package officina

import (
	"cmp"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"reflect"
	"strings"
)

// Output is the typed output an agent requires: a Go type whose JSON Schema the model is held to, and whose value a
// completed run returns as its Result's Output. The schema is part of the prefix.
type Output struct {
	schema   jsontext.Value
	compiled *schema
	typeName string
	decode   func(data []byte) (any, error)
}

// NewOutput returns the typed output of T, which must be a struct. Its schema is derived as NewTool derives a tool's
// input schema, and it fails for the same types, and for a type holding a map anywhere: structured output needs
// objects with named members.
func NewOutput[T any]() (*Output, error) {
	t := reflect.TypeFor[T]()
	if t.Kind() != reflect.Struct {
		return nil, fmt.Errorf("new output: %s is not a struct", t)
	}
	n, err := describe(t, nil)
	if err == nil {
		err = n.requireClosed("")
	}
	if err != nil {
		return nil, fmt.Errorf("new output: %w", err)
	}
	o := &Output{schema: n.json(), typeName: t.Name(), decode: func(data []byte) (any, error) {
		var v T
		err := json.Unmarshal(data, &v)
		return v, err //nolint:wrapcheck // read wraps it, with the type's name.
	}}
	if o.compiled, err = compileSchema(o.schema); err != nil {
		return nil, fmt.Errorf("new output: %w", err)
	}
	return o, nil
}

// requireClosed returns an error if n, at path, or a schema inside it is an open object, such as a map's.
func (n *node) requireClosed(path string) error {
	switch {
	case n.values != nil:
		return fmt.Errorf("the output schema at %q is an open object, such as a map's; typed output needs "+
			"structs", cmp.Or(path, "/"))
	case n.items != nil:
		return n.items.requireClosed(path + "/items")
	}
	for _, p := range n.props {
		if err := p.schema.requireClosed(path + "/properties/" + p.name); err != nil {
			return err
		}
	}
	return nil
}

// read returns the value of the reply text, or why it is not one: the text is not JSON, does not match the schema,
// or does not decode into the type.
func (o *Output) read(text string) (any, error) {
	var value any
	if err := json.Unmarshal([]byte(text), &value); err != nil {
		return nil, fmt.Errorf("the output could not be read as %s: %w", o.typeName, err)
	}
	if problems := o.compiled.validate(value); problems != nil {
		return nil, fmt.Errorf("the output does not match its schema: %s", strings.Join(problems, "; "))
	}
	v, err := o.decode([]byte(text))
	if err != nil {
		return nil, fmt.Errorf("the output could not be read as %s: %w", o.typeName, err)
	}
	return v, nil
}
