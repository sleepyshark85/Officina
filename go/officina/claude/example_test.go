package claude_test

import (
	"context"
	"fmt"
	"log"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

// An agent on Claude Opus 5.5 that answers one question. It needs an API key in ANTHROPIC_API_KEY, so the example is
// compiled but not run.
func ExampleNew() {
	model, err := claude.New(claude.Opus55, claude.EffortLow, claude.Options{MaxOutputTokens: 2_000})
	if err != nil {
		log.Fatal(err)
	}
	agent, err := officina.NewAgent(model, "Answer in one sentence.", officina.AgentOptions{})
	if err != nil {
		log.Fatal(err)
	}

	result, err := agent.Run(context.Background(), nil, "What is a bookshop?", officina.RunOptions{})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(result.Text)
}
