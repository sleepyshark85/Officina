package officinatest_test

import (
	"context"
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
