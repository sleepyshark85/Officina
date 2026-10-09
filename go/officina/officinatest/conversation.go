package officinatest

import (
	"errors"
	"fmt"
	"slices"

	"github.com/sleepyshark85/officina/go/officina"
)

// CheckConversation checks messages against the provider's rules, as a request or a stored conversation holds
// them. The roles: the conversation starts with a user message, no two messages in a row have one role, except a
// user message after one that holds only tool results (the provider joins them), and an operator message follows
// a user message and is last or followed by an assistant message. The tool calls: every call of a message is
// answered by the next message, once, in call order, and no result answers anything else.
func CheckConversation(messages []officina.Message) error {
	if len(messages) == 0 || messages[0].Role != officina.User {
		return errors.New("the first message is not the user's")
	}
	for i := 1; i < len(messages); i++ {
		previous, role := messages[i-1].Role, messages[i].Role
		switch {
		case role == previous && (role != officina.User || !onlyResults(messages[i-1])):
			return fmt.Errorf("messages %d and %d have one role, %s", i, i+1, role)
		case role == officina.Operator && previous != officina.User:
			return fmt.Errorf("operator message %d does not follow a user message", i+1)
		case previous == officina.Operator && role != officina.Assistant:
			return fmt.Errorf("operator message %d is followed by a %s message", i, role)
		}
	}
	var calls []string
	for i, m := range messages {
		var answers []string
		for _, b := range m.Blocks {
			if b.ToolResult != nil {
				answers = append(answers, b.ToolResult.CallID)
			}
		}
		if !slices.Equal(answers, calls) {
			return fmt.Errorf("message %d answers calls %q, want %q", i+1, answers, calls)
		}
		calls = nil
		for _, b := range m.Blocks {
			if b.ToolCall != nil {
				calls = append(calls, b.ToolCall.ID)
			}
		}
	}
	if calls != nil {
		return fmt.Errorf("the calls %q of the last message have no results", calls)
	}
	return nil
}

// onlyResults reports whether m holds tool results and nothing else.
func onlyResults(m officina.Message) bool {
	return len(m.Blocks) > 0 && !slices.ContainsFunc(m.Blocks, func(b officina.Block) bool { return b.ToolResult == nil })
}
