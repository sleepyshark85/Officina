package officina_test

import (
	"context"
	"encoding/json/v2"
	"fmt"

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
