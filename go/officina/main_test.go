package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"iter"
	"os"
	"path/filepath"
	"testing"

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
	return officina.Tool{Name: name, Description: description, InputSchema: jsontext.Value(`{"type":"object"}`)}
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

// sharedFile returns a file of the repository's testdata, which the .NET implementation reads too.
func sharedFile(t *testing.T, path ...string) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(append([]string{"..", "..", "testdata"}, path...)...))
	if err != nil {
		t.Fatalf("read shared testdata: %v", err)
	}
	return string(data)
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

func (f funcModel) Stream(ctx context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error] {
	return f(ctx, req)
}
