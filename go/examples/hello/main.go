package main

import (
	"bufio"
	"context"
	"fmt"
	"log"
	"os"
	"os/signal"
	"strings"
	"time"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	err := chat(ctx)
	stop()
	if err != nil {
		log.Fatal(err)
	}
}

func chat(ctx context.Context) error {
	model, err := claude.New(claude.Opus55, claude.EffortLow, claude.Options{
		MaxOutputTokens: 2_000, PrefixCache: claude.CacheOneHour, ConversationCache: claude.CacheOneHour,
	})
	if err != nil {
		return fmt.Errorf("hello: %w", err)
	}
	agent, err := officina.NewAgent(model, instructions(), officina.AgentOptions{})
	if err != nil {
		return fmt.Errorf("hello: %w", err)
	}
	var conversation officina.Conversation

	fmt.Println("Hello: chat with Claude. An empty line quits.")
	lines := bufio.NewScanner(os.Stdin)
	for lines.Scan() && lines.Text() != "" && ctx.Err() == nil {
		fmt.Println("> " + lines.Text())
		// The date is run context, sent after the message; the instructions never change.
		opts := officina.RunOptions{Context: "Today is " + time.Now().Format("Monday 2 January 2006") + "."}
		events, result := agent.Stream(ctx, &conversation, lines.Text(), opts)
		for event := range events {
			switch e := event.(type) {
			case officina.TextStreamed:
				fmt.Print(e.Text)
			case officina.ReplyRestarted:
				fmt.Println(" [the reply was interrupted and starts again]")
			}
		}
		res, err := result()
		if err != nil {
			return fmt.Errorf("hello: %w", err)
		}
		fmt.Println()
		fmt.Println(status(res))
	}
	if err := lines.Err(); err != nil {
		return fmt.Errorf("hello: read the input: %w", err)
	}
	return nil
}

func status(r officina.Result) string {
	outcome := "completed"
	switch r.Status {
	case officina.Stopped:
		outcome = fmt.Sprintf("stopped (%v) %s", r.Stop, r.Detail)
	case officina.Failed:
		outcome = fmt.Sprintf("failed (%v): %s", r.Failure, r.Detail)
	}
	u := r.Usage
	return fmt.Sprintf("[%s · input %d · cache read %d · cache write %d · output %d]",
		outcome, u.Input, u.CacheRead, u.CacheWrite, u.Output)
}

// instructions are frozen, and long enough to pass the model's minimum cacheable prefix.
func instructions() string {
	var b strings.Builder
	b.WriteString("You are Hello, a friendly assistant in a small demonstration of the Officina library. Answer in " +
		"one to three short sentences unless the user asks for more. Be warm, concrete and plain-spoken.\n\n" +
		"House rules:\n")
	for rule := 1; rule <= 40; rule++ {
		fmt.Fprintf(&b, "%d. When a question touches topic number %d, answer from general knowledge, say so when you "+
			"are unsure, never invent facts, figures or quotations, and offer one useful next step if it helps.\n",
			rule, rule)
	}
	return b.String()
}
