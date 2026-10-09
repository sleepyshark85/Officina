package claude

import (
	"bytes"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"slices"
)

// errOpenObject is an output schema with an object that allows unnamed properties.
var errOpenObject = errors.New("the output schema has an open object, which structured output cannot express")

// outputSchema returns a typed output schema as Claude's structured output takes it: every object closed with
// additionalProperties false, and the numeric bounds, maxItems and a minItems above 1 left out, as the API rejects
// them. The rest is kept as written, in its order, as the order of properties is the order the model writes them.
// The core still validates the reply against the schema as written. An open object, such as a map's, cannot be closed
// without changing its meaning, so it is refused. Not probed live: an enum holding null, and a true schema.
func outputSchema(schema jsontext.Value) (jsontext.Value, error) {
	if !schema.IsValid() {
		return nil, errors.New("the output schema is not valid JSON")
	}
	return adjust(schema)
}

// adjust adjusts a schema, valid JSON, and the schemas inside it.
func adjust(schema jsontext.Value) (jsontext.Value, error) {
	if schema.Kind() != '{' {
		return schema, nil
	}
	keywords, err := members(schema)
	if err != nil {
		return nil, err
	}
	b := []byte{'{'}
	object, closed := false, false
	for _, k := range keywords {
		value := k.value
		switch k.name {
		case "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "maxItems":
			continue
		case "minItems":
			var n float64
			if json.Unmarshal(value, &n) == nil && n > 1 {
				continue
			}
		case "additionalProperties":
			if value.Kind() != 'f' {
				return nil, errOpenObject
			}
			closed = true
		case "type":
			object = object || isObject(value)
		case "properties":
			object = true
			value, err = adjustMembers(value)
		case "items":
			value, err = adjust(value)
		case "anyOf":
			value, err = adjustItems(value)
		}
		if err != nil {
			return nil, err
		}
		if b, err = appendMember(b, k.name, value); err != nil {
			return nil, err
		}
	}
	if object && !closed {
		if b, err = appendMember(b, "additionalProperties", jsontext.Value("false")); err != nil {
			return nil, err
		}
	}
	return append(b, '}'), nil
}

// isObject reports whether a type keyword's value names the object type.
func isObject(types jsontext.Value) bool {
	var one string
	var many []string
	// The value is one name or a list of names; unmarshalling it as the other leaves that variable empty.
	_ = json.Unmarshal(types, &one)
	_ = json.Unmarshal(types, &many)
	return one == "object" || slices.Contains(many, "object")
}

// adjustMembers adjusts the schema of each member of properties; a value that is not an object is kept.
func adjustMembers(properties jsontext.Value) (jsontext.Value, error) {
	if properties.Kind() != '{' {
		return properties, nil
	}
	props, err := members(properties)
	if err != nil {
		return nil, err
	}
	b := []byte{'{'}
	for _, p := range props {
		value, err := adjust(p.value)
		if err != nil {
			return nil, err
		}
		if b, err = appendMember(b, p.name, value); err != nil {
			return nil, err
		}
	}
	return append(b, '}'), nil
}

// adjustItems adjusts each schema of anyOf; a value that is not an array is kept.
func adjustItems(anyOf jsontext.Value) (jsontext.Value, error) {
	var options []jsontext.Value
	if anyOf.Kind() != '[' {
		return anyOf, nil
	}
	if err := json.Unmarshal(anyOf, &options); err != nil {
		return nil, fmt.Errorf("read anyOf: %w", err)
	}
	b := []byte{'['}
	for i, option := range options {
		adjusted, err := adjust(option)
		if err != nil {
			return nil, err
		}
		if i > 0 {
			b = append(b, ',')
		}
		b = append(b, adjusted...)
	}
	return append(b, ']'), nil
}

// member is a member of a JSON object.
type member struct {
	name  string
	value jsontext.Value
}

// members returns the members of a JSON object, in order.
func members(object jsontext.Value) ([]member, error) {
	dec := jsontext.NewDecoder(bytes.NewReader(object))
	if _, err := dec.ReadToken(); err != nil {
		return nil, fmt.Errorf("read an object: %w", err)
	}
	var ms []member
	for dec.PeekKind() == '"' {
		token, err := dec.ReadToken()
		if err != nil {
			return nil, fmt.Errorf("read a name: %w", err)
		}
		// The token is valid only until the next read.
		name := token.String()
		value, err := dec.ReadValue()
		if err != nil {
			return nil, fmt.Errorf("read the value of %q: %w", name, err)
		}
		ms = append(ms, member{name, value.Clone()})
	}
	return ms, nil
}

// appendMember appends a member to the object b holds so far.
func appendMember(b []byte, name string, value jsontext.Value) ([]byte, error) {
	if len(b) > 1 {
		b = append(b, ',')
	}
	b, err := jsontext.AppendQuote(b, name)
	if err != nil {
		return nil, fmt.Errorf("write the name %q: %w", name, err)
	}
	return append(append(b, ':'), value...), nil
}
