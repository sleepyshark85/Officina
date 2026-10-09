package officina

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
)

// Validate compiles schema and returns the problems of input against it, for the black-box tests of the validator,
// which the API reaches only through a run. Its error is the schema's, or input's when it is not JSON.
func Validate(schema, input string) ([]string, error) {
	s, err := compileSchema(jsontext.Value(schema))
	if err != nil {
		return nil, err
	}
	var value any
	if err := json.Unmarshal([]byte(input), &value); err != nil {
		return nil, err
	}
	return s.validate(value), nil
}
