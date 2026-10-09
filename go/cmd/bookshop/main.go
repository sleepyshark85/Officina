package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
)

// composeDatabase is the database of the compose file in apps/BookshopAssistant, with its demo password.
const composeDatabase = "postgres://bookshop:shelf-demo-41@localhost:5432/bookshop"

func main() {
	os.Exit(run())
}

// run runs the application and returns its exit code.
func run() int {
	database := os.Getenv("BOOKSHOP_DATABASE")
	if database == "" {
		database = composeDatabase
	}
	model, err := bookshop.Model()
	if err != nil {
		return fail(err)
	}
	// Input that is not a terminal is not shown as it is typed, so the console writes each line after its prompt.
	stdin, err := os.Stdin.Stat()
	echo := err == nil && stdin.Mode()&os.ModeCharDevice == 0
	ctx := context.Background()
	app, err := bookshop.Build(ctx, bookshop.Config{
		Database: database, Model: model, In: os.Stdin, Out: os.Stdout, Echo: echo,
		// Ctrl+C stops the reply in progress and the session goes on. Between replies nothing listens for it, so it
		// ends the application, as usual.
		Interrupt: func(ctx context.Context) (context.Context, context.CancelFunc) {
			return signal.NotifyContext(ctx, os.Interrupt)
		},
	})
	if err != nil {
		return fail(err)
	}
	defer app.Close()
	if err := app.Run(ctx); err != nil {
		return fail(err)
	}
	return 0
}

// fail reports err and returns the exit code of a failure.
func fail(err error) int {
	fmt.Fprintln(os.Stderr, "bookshop:", err)
	return 1
}
