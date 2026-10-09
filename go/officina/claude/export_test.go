package claude

import "encoding/json/jsontext"

// ErrOpenObject is the error of an output schema with an open object, for the black-box fuzz test.
var ErrOpenObject = errOpenObject

// OutputSchema adjusts a typed output schema as a request does, for the black-box fuzz test, which the API reaches
// only through a request.
func OutputSchema(schema jsontext.Value) (jsontext.Value, error) {
	return outputSchema(schema)
}
