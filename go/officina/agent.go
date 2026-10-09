package officina

import (
	"cmp"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"slices"
	"strings"
)

// Agent is what an agent is: a model and instructions, and optionally tools. It is immutable once built, so any
// number of runs may share it at once.
type Agent struct {
	model        Model
	instructions string
	tools        []Tool
	fingerprint  string
}

// AgentOptions holds an agent's optional parts; the zero value has none.
type AgentOptions struct {
	// Tools may come in any order; the agent sorts them by name.
	Tools []Tool
}

// NewAgent returns an agent of model and instructions. The instructions are frozen for every conversation: nothing
// per user, run or date goes there. It fails if the instructions are blank, or a tool has no name, a name another
// tool has, or an input schema that is not a JSON object.
func NewAgent(model Model, instructions string, opts AgentOptions) (*Agent, error) {
	if model == nil {
		return nil, errors.New("new agent: no model")
	}
	if strings.TrimSpace(instructions) == "" {
		return nil, errors.New("new agent: blank instructions")
	}
	tools := make([]Tool, len(opts.Tools))
	for i, t := range opts.Tools {
		if t.Name == "" {
			return nil, fmt.Errorf("new agent: tool %d has no name", i+1)
		}
		if !t.InputSchema.IsValid() || t.InputSchema.Kind() != '{' {
			return nil, fmt.Errorf("new agent: tool %q: input schema is not a JSON object", t.Name)
		}
		t.InputSchema = t.InputSchema.Clone()
		tools[i] = t
	}
	slices.SortFunc(tools, func(a, b Tool) int { return cmp.Compare(a.Name, b.Name) })
	for i := 1; i < len(tools); i++ {
		if tools[i].Name == tools[i-1].Name {
			return nil, fmt.Errorf("new agent: two tools are named %q", tools[i].Name)
		}
	}
	fingerprint, err := prefixFingerprint(model.Settings(), instructions, tools)
	if err != nil {
		return nil, fmt.Errorf("new agent: %w", err)
	}
	return &Agent{model: model, instructions: instructions, tools: tools, fingerprint: fingerprint}, nil
}

// prefixFingerprint returns a SHA-256 hash of everything in the prefix that reaches the model, in a fixed encoding:
// stored conversations carry it, so the encoding never changes.
func prefixFingerprint(settings, instructions string, tools []Tool) (string, error) {
	type toolJSON struct {
		Name        string         `json:"name"`
		Description string         `json:"description"`
		InputSchema jsontext.Value `json:"inputSchema"`
	}
	prefix := struct {
		Model        string     `json:"model"`
		Instructions string     `json:"instructions"`
		Tools        []toolJSON `json:"tools"`
	}{Model: settings, Instructions: instructions, Tools: make([]toolJSON, len(tools))}
	for i, t := range tools {
		prefix.Tools[i] = toolJSON(t)
	}
	data, err := json.Marshal(prefix)
	if err != nil {
		return "", fmt.Errorf("fingerprint the prefix: %w", err)
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}
