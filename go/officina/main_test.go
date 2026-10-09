package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"iter"
	"os"
	"path/filepath"
	"slices"
	"sync"
	"testing"

	"github.com/google/go-cmp/cmp"
	"github.com/google/go-cmp/cmp/cmpopts"
	"go.uber.org/goleak"

	"github.com/sleepyshark85/officina/go/officina"
)

func TestMain(m *testing.M) {
	goleak.VerifyTestMain(m)
}

const instructions = "You are a helpful assistant."

// newAgent returns an agent of model with the test instructions and tools.
func newAgent(t *testing.T, model officina.Model, tools ...officina.Tool) *officina.Agent {
	t.Helper()
	agent, err := officina.NewAgent(model, instructions, officina.AgentOptions{Tools: tools})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	return agent
}

// tool returns a tool with a small input schema.
func tool(name, description string) officina.Tool {
	return officina.Tool{
		Name: name, Description: description, InputSchema: jsontext.Value(`{"type":"object"}`), Kind: officina.Read,
		Handler: func(context.Context, jsontext.Value) (string, error) { return "ok", nil },
	}
}

// ignoreHandler compares tools without their handlers, as funcs never compare equal, and without what is unexported.
func ignoreHandler() cmp.Option {
	return cmp.Options{cmpopts.IgnoreFields(officina.Tool{}, "Handler"), cmpopts.IgnoreUnexported(officina.Tool{})}
}

// outcome compares results by how the run ended, without their cost, counts and duration.
func outcome() cmp.Option {
	return cmpopts.IgnoreFields(officina.Result{}, "Cost", "ModelCalls", "ToolCalls", "Duration")
}

// run runs agent on c and fails the test on an error.
func run(t *testing.T, agent *officina.Agent, c *officina.Conversation, message string, opts officina.RunOptions) officina.Result {
	t.Helper()
	result, err := agent.Run(t.Context(), c, message, opts)
	if err != nil {
		t.Fatalf("Run(%q) error = %v", message, err)
	}
	return result
}

// stream runs agent on c and returns its events and result, failing the test on an error.
func stream(ctx context.Context, t *testing.T, agent *officina.Agent, c *officina.Conversation, message string,
	opts officina.RunOptions, each func(officina.RunEvent) bool,
) ([]officina.RunEvent, officina.Result) {
	t.Helper()
	events, result := agent.Stream(ctx, c, message, opts)
	var got []officina.RunEvent
	for event := range events {
		got = append(got, event)
		if !each(event) {
			break
		}
	}
	res, err := result()
	if err != nil {
		t.Fatalf("Stream(%q) error = %v", message, err)
	}
	return got, res
}

// shared returns the path of a file of the repository's testdata: under OFFICINA_TESTDATA when it is set, as for
// mutation testing, which runs the tests in a copy of go/ alone; else beside the module.
func shared(path ...string) string {
	dir := os.Getenv("OFFICINA_TESTDATA")
	if dir == "" {
		dir = filepath.Join("..", "..", "testdata")
	}
	return filepath.Join(append([]string{dir}, path...)...)
}

// sharedFile returns a file of the repository's testdata, which the .NET implementation reads too.
func sharedFile(t *testing.T, path ...string) string {
	t.Helper()
	data, err := os.ReadFile(shared(path...))
	if err != nil {
		t.Fatalf("read shared testdata: %v", err)
	}
	return string(data)
}

// results returns the tool results of a message.
func results(m officina.Message) []officina.ToolResult {
	var got []officina.ToolResult
	for _, b := range m.Blocks {
		if b.ToolResult != nil {
			got = append(got, *b.ToolResult)
		}
	}
	return got
}

// handlerTool returns a tool of kind with an object schema that runs handler.
func handlerTool(name string, kind officina.ToolKind,
	handler func(ctx context.Context, input jsontext.Value) (string, error),
) officina.Tool {
	return officina.Tool{Name: name, InputSchema: jsontext.Value(`{"type":"object"}`), Kind: kind, Handler: handler}
}

// memorySink is an audit sink that keeps its entries, and fails a write when fail says so.
type memorySink struct {
	fail func(officina.AuditEntry) bool

	mu      sync.Mutex
	entries []officina.AuditEntry
}

func (s *memorySink) Write(_ context.Context, e officina.AuditEntry) error {
	if s.fail != nil && s.fail(e) {
		return errors.New("the disk is full")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.entries = append(s.entries, e)
	return nil
}

// Entries returns the entries written so far.
func (s *memorySink) Entries() []officina.AuditEntry {
	s.mu.Lock()
	defer s.mu.Unlock()
	return slices.Clone(s.entries)
}

// roles returns the role of each message.
func roles(messages []officina.Message) []officina.Role {
	var got []officina.Role
	for _, m := range messages {
		got = append(got, m.Role)
	}
	return got
}

// funcModel is a model whose stream is a function, for behaviour the scripted model does not script.
type funcModel func(ctx context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error]

func (funcModel) Settings() string { return "func" }

func (funcModel) Info() officina.ModelInfo { return officina.ModelInfo{Provider: "func", Name: "func"} }

func (f funcModel) Stream(ctx context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error] {
	return f(ctx, req)
}
