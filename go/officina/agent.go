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
	"unicode/utf16"
	"unicode/utf8"

	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"
)

// Agent is what an agent is: a model and instructions, and optionally tools, an approver and an audit sink. It is
// immutable once built, so any number of runs may share it at once.
type Agent struct {
	model        Model
	instructions string
	tools        []Tool
	// schemas holds each tool's compiled input schema, in the order of tools.
	schemas []*schema
	// sources are the tools' distinct sources, which each run connects.
	sources   []ToolSource
	approver  Approver
	auditSink AuditSink
	name      string
	// secrets holds every form of each secret that is redacted.
	secrets           []string
	fingerprint       string
	telemetry         *telemetry
	contextManagement ContextManagement
	output            *Output
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
	// TracerProvider and MeterProvider receive the agent's traces and metrics; without them it emits none. The
	// agent never uses the global providers.
	TracerProvider trace.TracerProvider
	MeterProvider  metric.MeterProvider
	// TelemetryContent puts message text and tool inputs and results in the spans, with the secrets redacted. By
	// default telemetry holds none: it is for operation, and the audit trail is for the record.
	TelemetryContent bool
	// ContextManagement is how the model's provider shortens a long conversation; none by default. It needs the
	// provider's support (ModelInfo), and is part of the prefix. Without compaction, a run that fills the model's
	// context window stops with ContextFull.
	ContextManagement ContextManagement
	// Output is the typed output the agent's runs return, from NewOutput; without it, a run's result is its text.
	// Its schema is part of the prefix.
	Output *Output
}

// NewAgent returns an agent of model and instructions. The instructions are frozen for every conversation: nothing
// per user, run or date goes there. It fails if the instructions are blank, or a tool has no name, a name another
// tool has, no kind, no handler, or an input schema that is not an object schema in the subset the core validates;
// or if the context management is invalid or needs what the model's provider does not do.
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
	sources, err := sourcesOf(tools)
	if err != nil {
		return nil, fmt.Errorf("new agent: %w", err)
	}
	if err := opts.ContextManagement.check(model.Info()); err != nil {
		return nil, fmt.Errorf("new agent: %w", err)
	}
	a := &Agent{
		model: model, instructions: instructions, tools: tools, schemas: schemas, sources: sources,
		approver: opts.Approver, auditSink: opts.AuditSink, name: opts.Name, secrets: secretForms(opts.Secrets),
		fingerprint:       prefixFingerprint(model.Settings(), instructions, tools, opts.Output, opts.ContextManagement),
		contextManagement: opts.ContextManagement, output: opts.Output,
	}
	a.telemetry, err = newTelemetry(opts.TracerProvider, opts.MeterProvider, opts.Name, model.Info(),
		opts.TelemetryContent, a.redact)
	if err != nil {
		return nil, fmt.Errorf("new agent: %w", err)
	}
	return a, nil
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

// secretForms returns each secret as written and as a JSON string may escape it: with or without <, > and &
// escaped, with every character beyond ASCII escaped in lower or upper case hex, and with / as written or escaped;
// sorted and without duplicates.
func secretForms(secrets []string) []string {
	var forms []string
	for _, s := range secrets {
		if s == "" {
			continue
		}
		forms = append(forms, s)
		for _, escape := range []json.Options{jsontext.EscapeForHTML(false), jsontext.EscapeForHTML(true)} {
			// Only invalid UTF-8 fails, and such a secret cannot appear in a JSON string.
			quoted, err := json.Marshal(s, escape)
			if err != nil {
				continue
			}
			inner := string(quoted[1 : len(quoted)-1])
			forms = append(forms, inner, asciiOnly(inner, "%04x"), asciiOnly(inner, "%04X"))
		}
	}
	for _, f := range slices.Clone(forms) {
		forms = append(forms, strings.ReplaceAll(f, "/", `\/`))
	}
	slices.Sort(forms)
	return slices.Compact(forms)
}

// asciiOnly returns the inside of a JSON string with each character beyond ASCII escaped as a backslash, u and its
// UTF-16 code units in hex, written with format.
func asciiOnly(inner, format string) string {
	var b strings.Builder
	for _, r := range inner {
		if r < utf8.RuneSelf {
			b.WriteRune(r)
			continue
		}
		units := []rune{r}
		if r1, r2 := utf16.EncodeRune(r); r1 != utf8.RuneError {
			units = []rune{r1, r2}
		}
		for _, u := range units {
			b.WriteString(`\` + "u" + fmt.Sprintf(format, u))
		}
	}
	return b.String()
}

// redact returns text with every stretch that holds a form of a secret replaced by "[redacted]". It finds every
// occurrence of every form in the original text and merges those that overlap or touch, so secrets that share
// characters are redacted whole whatever their order.
func (a *Agent) redact(text string) string {
	var spans [][2]int
	for at := range len(text) {
		for _, form := range a.secrets {
			if strings.HasPrefix(text[at:], form) {
				spans = append(spans, [2]int{at, at + len(form)})
			}
		}
	}
	if spans == nil {
		return text
	}
	slices.SortFunc(spans, func(x, y [2]int) int { return cmp.Compare(x[0], y[0]) })
	var b strings.Builder
	done := 0
	for k := 0; k < len(spans); {
		start, end := spans[k][0], spans[k][1]
		for k++; k < len(spans) && spans[k][0] <= end; k++ {
			end = max(end, spans[k][1])
		}
		b.WriteString(text[done:start])
		b.WriteString("[redacted]")
		done = end
	}
	b.WriteString(text[done:])
	return b.String()
}

// prefixFingerprint returns a SHA-256 hash of everything in the prefix that reaches the model. Stored conversations
// carry it, and a conversation either implementation stored resumes in the other, so it hashes the bytes the .NET
// implementation hashes, which must never change: {"model":…,"instructions":…,"tools":[{"name":…,"description":…,
// "inputSchema":…},…],"output":…,"contextManagement":{…}}, with the strings escaped as .NET's default JSON encoder
// escapes them, each schema as given, the output schema only with typed output, and the context management only
// when it asks for something.
func prefixFingerprint(settings, instructions string, tools []Tool, output *Output, cm ContextManagement) string {
	b := appendDotnetString([]byte(`{"model":`), settings)
	b = appendDotnetString(append(b, `,"instructions":`...), instructions)
	b = append(b, `,"tools":[`...)
	for i, t := range tools {
		if i > 0 {
			b = append(b, ',')
		}
		b = appendDotnetString(append(b, `{"name":`...), t.Name)
		b = appendDotnetString(append(b, `,"description":`...), t.Description)
		b = append(append(append(b, `,"inputSchema":`...), t.InputSchema...), '}')
	}
	b = append(b, ']')
	if output != nil {
		b = append(append(b, `,"output":`...), output.schema...)
	}
	sum := sha256.Sum256(append(cm.appendFingerprint(b), '}'))
	return hex.EncodeToString(sum[:])
}

// appendDotnetString appends s to b as a JSON string escaped as .NET's default JSON encoder escapes it: a backslash
// doubled; a backspace, tab, line feed, form feed and carriage return by their letters; and every other control
// character, each of " & ' + < > and the backquote, and every character beyond ASCII as its UTF-16 code units in
// upper-case hex. Invalid UTF-8 is written as U+FFFD.
func appendDotnetString(b []byte, s string) []byte {
	const shorts, letters = "\b\t\n\f\r", "btnfr"
	b = append(b, '"')
	for _, r := range s {
		switch short := strings.IndexRune(shorts, r); {
		case r == '\\':
			b = append(b, '\\', '\\')
		case short >= 0:
			b = append(b, '\\', letters[short])
		case r >= ' ' && r < utf8.RuneSelf-1 && !strings.ContainsRune("\"&'+<>`", r):
			b = append(b, byte(r))
		default:
			for _, unit := range utf16.AppendRune(nil, r) {
				b = fmt.Appendf(append(b, '\\', 'u'), "%04X", unit)
			}
		}
	}
	return append(b, '"')
}
