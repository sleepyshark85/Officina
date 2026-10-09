package officina

import "encoding/json/jsontext"

// Tool describes an action the model may request. All of it reaches the model, so it is part of the prefix.
type Tool struct {
	// Name is unique among an agent's tools.
	Name        string
	Description string
	// InputSchema is the JSON Schema of the tool's input: a JSON object.
	InputSchema jsontext.Value
}
