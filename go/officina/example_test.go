package officina_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"
	"os"
	"path/filepath"
	"strings"

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
