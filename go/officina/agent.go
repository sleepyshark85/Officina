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

// Agent is what an agent is: a model and instructions, and optionally tools, an approver and an audit sink. It is
// immutable once built, so any number of runs may share it at once.
type Agent struct {
	model        Model
	instructions string
	tools        []Tool
	// schemas holds each tool's compiled input schema, in the order of tools.
	schemas     []*schema
	approver    Approver
	auditSink   AuditSink
	name        string
	secrets     *strings.Replacer
	fingerprint string
}

// AgentOptions holds an agent's optional parts; the zero value has none.
type AgentOptions struct {
	// Tools may come in any order; the agent sorts them by name.
	Tools []Tool
	// Approver answers approval requests; without one, runs are unattended and calls needing approval are denied.
	Approver Approver
	// AuditSink is where the audit trail goes; without one there is no trail, and nothing else changes.
	AuditSink AuditSink
	// Name names the agent in the audit trail; it is not sent to the model.
	Name string
	// Secrets are values that must never reach events, tool results or the audit trail, such as a tool's database
	// password. They are redacted there, as written and as escaped in a JSON string.
	Secrets []string
}

// NewAgent returns an agent of model and instructions. The instructions are frozen for every conversation: nothing
// per user, run or date goes there. It fails if the instructions are blank, or a tool has no name, a name another
// tool has, no kind, no handler, or an input schema that is not an object schema in the subset the core validates.
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
		if t.Kind != Read && t.Kind != Write {
			return nil, fmt.Errorf("new agent: tool %q: kind is neither Read nor Write", t.Name)
		}
		if t.Handler == nil {
			return nil, fmt.Errorf("new agent: tool %q has no handler", t.Name)
		}
		t.InputSchema = t.InputSchema.Clone()
		tools[i] = t
	}
	slices.SortFunc(tools, func(a, b Tool) int { return cmp.Compare(a.Name, b.Name) })
	schemas := make([]*schema, len(tools))
	for i, t := range tools {
		if i > 0 && t.Name == tools[i-1].Name {
			return nil, fmt.Errorf("new agent: two tools are named %q", t.Name)
		}
		s, err := compileSchema(t.InputSchema)
		if err == nil && !slices.Equal(s.types, []string{"object"}) {
			err = errors.New(`the input schema's type is not "object"`)
		}
		if err != nil {
			return nil, fmt.Errorf("new agent: tool %q: %w", t.Name, err)
		}
		schemas[i] = s
	}
	fingerprint, err := prefixFingerprint(model.Settings(), instructions, tools)
	if err != nil {
		return nil, fmt.Errorf("new agent: %w", err)
	}
	return &Agent{
		model: model, instructions: instructions, tools: tools, schemas: schemas, approver: opts.Approver,
		auditSink: opts.AuditSink, name: opts.Name, secrets: replacer(opts.Secrets), fingerprint: fingerprint,
	}, nil
}

// tool is one of an agent's tools with its compiled input schema.
type tool struct {
	Tool
	schema *schema
}

// tool returns the agent's tool of that name.
func (a *Agent) tool(name string) (tool, bool) {
	i, found := slices.BinarySearchFunc(a.tools, name, func(t Tool, name string) int { return cmp.Compare(t.Name, name) })
	if !found {
		return tool{}, false
	}
	return tool{a.tools[i], a.schemas[i]}, true
}

// replacer returns a replacer of each secret, as written and as escaped in a JSON string, by "[redacted]"; nil
// when there are none.
func replacer(secrets []string) *strings.Replacer {
	var pairs []string
	for _, s := range secrets {
		if s == "" {
			continue
		}
		pairs = append(pairs, s, "[redacted]")
		for _, escape := range []json.Options{jsontext.EscapeForHTML(false), jsontext.EscapeForHTML(true)} {
			// Only invalid UTF-8 fails, and such a secret cannot appear in a JSON string.
			if quoted, err := json.Marshal(s, escape); err == nil {
				pairs = append(pairs, string(quoted[1:len(quoted)-1]), "[redacted]")
			}
		}
	}
	if pairs == nil {
		return nil
	}
	return strings.NewReplacer(pairs...)
}

// redact returns text with the agent's secrets replaced.
func (a *Agent) redact(text string) string {
	if a.secrets == nil {
		return text
	}
	return a.secrets.Replace(text)
}

// prefixFingerprint returns a SHA-256 hash of everything in the prefix that reaches the model. Stored
// conversations carry it, so once Go S08 aligns it with .NET's it must not change. Until then it may: it uses .NET's
// keys but not yet its escaping, and the schema is re-encoded, so schemas that differ only in spacing or escapes
// share a fingerprint.
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
		prefix.Tools[i] = toolJSON{Name: t.Name, Description: t.Description, InputSchema: t.InputSchema}
	}
	data, err := json.Marshal(prefix)
	if err != nil {
		return "", fmt.Errorf("fingerprint the prefix: %w", err)
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}
