package bookshop

import (
	"fmt"
	"strings"
	"unicode/utf8"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

// summary is what the summarizer writes about a session.
type summary struct {
	Title   string   `json:"title" jsonschema:"A title of a few words, naming the customers, books or orders the session was about."`
	Summary string   `json:"summary" jsonschema:"One to three sentences on what the staff member asked and what came of it."`
	Changes []string `json:"changes" jsonschema:"Each change made to the shop's data, such as a customer added, an order placed or cancelled, or a restock, with its ids. Empty when nothing changed."`
}

// summaryBudget is the most one summary may spend, in US dollars. It limits output, not input: a long session's
// summary may cost more.
const summaryBudget = 0.05

// resultLength is the most characters of one tool result a transcript keeps.
const resultLength = 1_000

// newSummarizer returns the session summarizer: a stateless agent of model with typed output and no tools, with
// cfg's telemetry. It reads a session's transcript as one user message.
func newSummarizer(cfg Config, model officina.Model) (*officina.Agent, error) {
	output, err := officina.NewOutput[summary]()
	if err != nil {
		return nil, fmt.Errorf("summarizer: %w", err)
	}
	agent, err := officina.NewAgent(model, summarizerInstructions, officina.AgentOptions{
		Name: "summarizer", Output: output, TracerProvider: cfg.TracerProvider, MeterProvider: cfg.MeterProvider,
	})
	if err != nil {
		return nil, fmt.Errorf("summarizer: %w", err)
	}
	return agent, nil
}

// SummarizerModel returns the session summarizer's Claude model: a short, typed answer needs little effort. The API
// key comes from the environment (ANTHROPIC_API_KEY), as the SDK finds it.
func SummarizerModel() (*claude.Model, error) {
	model, err := claude.New(claude.Opus55, claude.EffortLow, claude.Options{MaxOutputTokens: 4_000})
	if err != nil {
		return nil, fmt.Errorf("summarizer model: %w", err)
	}
	return model, nil
}

// transcript returns a session's transcript as the summarizer reads it: the staff member's messages, the assistant's
// replies, and each tool call with its input and outcome, one a line; without the run context.
func transcript(c *officina.Conversation) string {
	calls := map[string]string{}
	var b strings.Builder
	for _, m := range c.Messages() {
		if m.Role == officina.Operator {
			continue
		}
		for _, block := range m.Blocks {
			switch call, result := block.ToolCall, block.ToolResult; {
			case call != nil:
				calls[call.ID] = call.Name
				b.WriteString("Tool call " + call.Name + " " + string(call.Input) + "\n")
			case result != nil:
				name, ok := calls[result.CallID]
				if !ok {
					name = "a call"
				}
				if result.IsError {
					name += " (error)"
				}
				b.WriteString("Tool result of " + name + ": " + cut(result.Content) + "\n")
			case block.Text != "" && m.Role == officina.User:
				b.WriteString("Staff: " + block.Text + "\n")
			case block.Text != "":
				b.WriteString("Assistant: " + block.Text + "\n")
			}
		}
	}
	return b.String()
}

// cut returns text, or its first resultLength characters and a mark that it goes on.
func cut(text string) string {
	if utf8.RuneCountInString(text) <= resultLength {
		return text
	}
	return string([]rune(text)[:resultLength]) + " […]"
}

// summarizerInstructions are the summarizer's frozen instructions, the same as the .NET implementation's.
const summarizerInstructions = `You summarize a session between a member of staff of a small bookshop and the Bookshop Assistant, a chatbot
that looks up and changes the shop's catalogue, stock, customers and orders. The user message is the session's
transcript: the staff member's messages, the assistant's replies, and each tool call with its outcome.

Write a title, a summary and the list of changes made. Count as changes only write tool calls that succeeded
(adding a customer, placing or cancelling an order, restocking); a declined or failed call changed nothing.
Name customers, books and orders with their ids. Write in British English, plainly.`
