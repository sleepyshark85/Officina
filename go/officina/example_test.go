package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	"go.opentelemetry.io/otel/sdk/trace/tracetest"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// A stateful run: the host keeps the conversation, here as JSON, between runs.
func ExampleAgent_Run() {
	model := officinatest.NewModel("scripted", officinatest.TextReply("Paris."))
	agent, err := officina.NewAgent(model, "You answer geography questions.", officina.AgentOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}
	var c officina.Conversation

	result, err := agent.Run(context.Background(), &c, "Capital of France?", officina.RunOptions{Context: "Date: 2026-10-09."})
	if err != nil {
		fmt.Println(err)
		return
	}
	saved, err := json.Marshal(&c)
	if err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(result.Status == officina.Completed, result.Text)
	fmt.Println(len(c.Messages()), "messages,", len(saved) > 0)
	// Output:
	// true Paris.
	// 3 messages, true
}

// Telemetry: the host passes its providers, here the OpenTelemetry SDK's with an in-memory exporter; an
// application passes ones with an OTLP exporter. Each run is a trace, with a span per model call and tool call.
func Example_telemetry() {
	spans := tracetest.NewInMemoryExporter()
	traces := sdktrace.NewTracerProvider(sdktrace.WithSyncer(spans))
	defer func() { _ = traces.Shutdown(context.Background()) }() // In memory, nothing to flush.
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello!"))
	agent, err := officina.NewAgent(model, "You are a helpful assistant.", officina.AgentOptions{
		Name: "greeter", TracerProvider: traces,
	})
	if err != nil {
		fmt.Println(err)
		return
	}

	if _, err := agent.Run(context.Background(), nil, "Hi", officina.RunOptions{}); err != nil {
		fmt.Println(err)
		return
	}

	for _, s := range spans.GetSpans() {
		fmt.Println(s.Name)
	}
	// Output:
	// chat scripted
	// invoke_agent greeter
}

// Streaming a run: text as it arrives, each append to save the conversation, then the result.
func ExampleAgent_Stream() {
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello!"))
	agent, err := officina.NewAgent(model, "You are a helpful assistant.", officina.AgentOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}

	events, result := agent.Stream(context.Background(), nil, "Hi", officina.RunOptions{})
	for event := range events {
		switch e := event.(type) {
		case officina.TextStreamed:
			fmt.Println("text:", e.Text)
		case officina.ConversationAppended:
			fmt.Println("appended:", e.Message.Role)
		}
	}
	res, err := result()
	if err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(res.Status == officina.Completed, res.Text)
	// Output:
	// text: Hello!
	// appended: user
	// appended: assistant
	// true Hello!
}

// A typed tool: its input schema comes from the input struct, and the run calls it, then the model again.
func ExampleNewTool() {
	type query struct {
		Title string `json:"title" jsonschema:"The book's title, or part of it."`
	}
	search, err := officina.NewTool("search", "Searches the catalogue.", officina.Read,
		func(_ context.Context, q query) (string, error) { return q.Title + ": 3 in stock", nil })
	if err != nil {
		fmt.Println(err)
		return
	}
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", `{"title":"Emma"}`)),
		officinatest.TextReply("Emma is in stock."))
	agent, err := officina.NewAgent(model, "You help the staff of a bookshop.",
		officina.AgentOptions{Tools: []officina.Tool{search}})
	if err != nil {
		fmt.Println(err)
		return
	}

	events, result := agent.Stream(context.Background(), nil, "Is Emma in stock?", officina.RunOptions{})
	for event := range events {
		if e, ok := event.(officina.ToolCallFinished); ok {
			fmt.Println(e.Call.Name, "->", e.Result.Content)
		}
	}
	res, err := result()
	if err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(string(search.InputSchema))
	fmt.Println(res.Text)
	// Output:
	// search -> Emma: 3 in stock
	// {"type":"object","properties":{"title":{"type":"string","description":"The book's title, or part of it."}},"required":["title"],"additionalProperties":false}
	// Emma is in stock.
}

// Typed output: the model is held to the schema of a struct, and a completed run returns a value of it.
func ExampleNewOutput() {
	type order struct {
		Customer string `json:"customer"`
		Copies   int    `json:"copies" jsonschema:"How many copies."`
	}
	output, err := officina.NewOutput[order]()
	if err != nil {
		fmt.Println(err)
		return
	}
	model := officinatest.NewModel("scripted", officinatest.TextReply(`{"customer":"Ana","copies":2}`))
	agent, err := officina.NewAgent(model, "Read the order in the message.", officina.AgentOptions{Output: output})
	if err != nil {
		fmt.Println(err)
		return
	}

	res, err := agent.Run(context.Background(), nil, "Ana wants two copies of Emma.", officina.RunOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(string(model.Requests()[0].OutputSchema))
	fmt.Printf("%+v\n", res.Output.(order))
	// Output:
	// {"type":"object","properties":{"customer":{"type":"string"},"copies":{"type":"integer","description":"How many copies."}},"required":["customer","copies"],"additionalProperties":false}
	// {Customer:Ana Copies:2}
}

// An audit trail in a JSON-lines file, which the host chooses: a write tool runs only once its attempt is recorded.
func ExampleNewJSONLinesSink() {
	dir, err := os.MkdirTemp("", "audit")
	if err != nil {
		fmt.Println(err)
		return
	}
	defer os.RemoveAll(dir) //nolint:errcheck // A temporary directory.
	order := officina.Tool{
		Name: "order", Description: "Orders a book.", InputSchema: jsontext.Value(`{"type":"object"}`),
		Kind: officina.Write, NeedsApproval: true,
		Handler: func(context.Context, jsontext.Value) (string, error) { return "ordered", nil },
	}
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "order", `{}`)), officinatest.TextReply("Ordered."))
	agent, err := officina.NewAgent(model, "You help the staff of a bookshop.", officina.AgentOptions{
		Tools: []officina.Tool{order}, AuditSink: officina.NewJSONLinesSink(filepath.Join(dir, "audit.jsonl")),
		Approver: officinatest.NewApprover(officina.Approval{Approved: true}), Name: "bookshop",
	})
	if err != nil {
		fmt.Println(err)
		return
	}

	if _, err := agent.Run(context.Background(), nil, "Order Emma", officina.RunOptions{}); err != nil {
		fmt.Println(err)
		return
	}
	data, err := os.ReadFile(filepath.Join(dir, "audit.jsonl"))
	if err != nil {
		fmt.Println(err)
		return
	}

	for line := range strings.Lines(string(data)) {
		var e struct {
			Sequence      int
			Kind, Outcome string
		}
		if err := json.Unmarshal([]byte(line), &e, json.MatchCaseInsensitiveNames(true)); err != nil {
			fmt.Println(err)
			return
		}
		fmt.Println(strings.TrimSpace(fmt.Sprint(e.Sequence, " ", e.Kind, " ", e.Outcome)))
	}
	// Output:
	// 1 RunStarted
	// 2 ApprovalAsked
	// 3 ApprovalAnswered approved
	// 4 ToolStarted
	// 5 ToolEnded ok
	// 6 RunEnded Completed
}

// A budget: each run gets the limits it may use, here one model call. The run stops before the call the budget does
// not allow, and says why.
func ExampleLimits() {
	search := officina.Tool{
		Name: "search", Kind: officina.Read, InputSchema: jsontext.Value(`{"type":"object"}`),
		Handler: func(context.Context, jsontext.Value) (string, error) { return "3 copies", nil },
	}
	model := officinatest.NewModel("scripted", officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "search", `{}`)))
	agent, err := officina.NewAgent(model, "You answer stock questions.", officina.AgentOptions{
		Tools: []officina.Tool{search},
	})
	if err != nil {
		fmt.Println(err)
		return
	}

	result, err := agent.Run(context.Background(), nil, "Is Emma in stock?", officina.RunOptions{
		Budget: officina.Limits{ModelCalls: new(1)},
	})
	if err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(result.Stop, result.ModelCalls, result.ToolCalls)
	fmt.Println(result.Detail)
	// Output:
	// Budget 1 1
	// the model call budget is used up: 1 of 1
}

// A stored conversation goes on only with an agent of the same tools, instructions and model settings; another
// agent's run on it would fail with a prefix mismatch.
func ExampleAgent_CanContinue() {
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."))
	agent, err := officina.NewAgent(model, "You are a helpful assistant.", officina.AgentOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}
	changed, err := officina.NewAgent(model, "You are a terse assistant.", officina.AgentOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}
	var c officina.Conversation
	if _, err := agent.Run(context.Background(), &c, "Hi", officina.RunOptions{}); err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(agent.CanContinue(&c), changed.CanContinue(&c))
	// Output:
	// true false
}
