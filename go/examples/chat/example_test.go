package chat_test

import (
	"context"
	"fmt"

	"github.com/sleepyshark85/officina/go/examples/chat"
	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// A user's two messages offline, the scripted model in place of Claude: the assistant converts with its tool, and the
// user's conversation is kept as JSON between the replies.
func ExampleAssistant_Reply() {
	ctx := context.Background()
	model := officinatest.NewModel("scripted",
		officinatest.ToolUseReply(officinatest.ToolUseBlock("t1", "celsius_to_fahrenheit", `{"celsius":20}`)),
		officinatest.TextReply("20 °C is 68 °F."),
		officinatest.TextReply("You asked about 20 °C."))
	agent, err := chat.NewAgent(model, &officina.MapMemoryStore{})
	if err != nil {
		fmt.Println(err)
		return
	}
	conversations := map[string][]byte{}
	assistant := chat.NewAssistant(agent, conversations)

	for _, message := range []string{"What is 20 °C in Fahrenheit?", "What did I ask?"} {
		res, err := assistant.Reply(ctx, "ana", message)
		if err != nil {
			fmt.Println(err)
			return
		}
		fmt.Println(res.Text)
	}
	fmt.Println(len(conversations), "conversation; prefix problems:", officinatest.CheckPrefix(model.Requests()))
	// Output:
	// 20 °C is 68 °F.
	// You asked about 20 °C.
	// 1 conversation; prefix problems: <nil>
}
