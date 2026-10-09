package officinatest_test

import (
	"context"
	"encoding/json/jsontext"
	"fmt"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// A scripted multi-turn run, checked for a stable prefix.
func ExampleCheckPrefix() {
	model := officinatest.NewModel("scripted", officinatest.TextReply("Hello."), officinatest.TextReply("Paris."))
	agent, err := officina.NewAgent(model, "You are a helpful assistant.", officina.AgentOptions{})
	if err != nil {
		fmt.Println(err)
		return
	}
	var c officina.Conversation
	for _, message := range []string{"Hi", "Capital of France?"} {
		if _, err := agent.Run(context.Background(), &c, message, officina.RunOptions{}); err != nil {
			fmt.Println(err)
			return
		}
	}

	fmt.Println(len(model.Requests()), "requests, prefix problems:", officinatest.CheckPrefix(model.Requests()))
	// Output: 2 requests, prefix problems: <nil>
}

// A scripted human denies a call; the conversation stays valid, every call answered.
func ExampleNewApprover() {
	order := officina.Tool{
		Name: "order", InputSchema: jsontext.Value(`{"type":"object"}`), Kind: officina.Write, NeedsApproval: true,
		Handler: func(context.Context, jsontext.Value) (string, error) { return "ordered", nil },
	}
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("c1", "order", `{}`)), officinatest.TextReply("Not ordered."))
	approver := officinatest.NewApprover(officina.Approval{Reason: "the budget is spent"})
	agent, err := officina.NewAgent(model, "You help the staff of a bookshop.",
		officina.AgentOptions{Tools: []officina.Tool{order}, Approver: approver})
	if err != nil {
		fmt.Println(err)
		return
	}
	var c officina.Conversation

	if _, err := agent.Run(context.Background(), &c, "Order Emma", officina.RunOptions{}); err != nil {
		fmt.Println(err)
		return
	}

	fmt.Println(len(approver.Asked()), "asked;", c.Messages()[2].Blocks[0].ToolResult.Content)
	fmt.Println("conversation problems:", officinatest.CheckConversation(c.Messages()))
	// Output:
	// 1 asked; The call was denied: the budget is spent
	// conversation problems: <nil>
}
