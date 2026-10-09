package main

import (
	"context"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"sync"
	"time"

	"go.opentelemetry.io/otel"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
	"github.com/sleepyshark85/officina/go/officina"
)

// The compose file's services, in apps/BookshopAssistant: the database, with its demo password, the telemetry
// dashboard, with its OTLP endpoint, and the export server.
const (
	composeDatabase  = "postgres://bookshop:shelf-demo-41@localhost:5432/bookshop"
	composeDashboard = "http://localhost:18888"
	composeOTLP      = "http://localhost:4317"
	composeExports   = "http://localhost:18800/mcp"
)

func main() {
	os.Exit(run())
}

// run runs the application and returns its exit code.
func run() int {
	ctx := context.Background()
	telemetry, err := bookshop.NewTelemetry(ctx, setting("OTEL_EXPORTER_OTLP_ENDPOINT", composeOTLP))
	if err != nil {
		return fail(err)
	}
	defer func() {
		ctx, cancel := context.WithTimeout(ctx, 5*time.Second)
		defer cancel()
		// The session is over; telemetry the dashboard did not take is lost, as it would be on a crash.
		_ = telemetry.Close(ctx)
	}()
	// An export that fails, as when the dashboard is not running, is told once, not on every export.
	var once sync.Once
	otel.SetErrorHandler(otel.ErrorHandlerFunc(func(err error) {
		once.Do(func() { fmt.Fprintln(os.Stderr, "bookshop: telemetry not exported:", err) })
	}))

	demo := flag.Bool("demo", false, "compact and clear early enough to see in a short session")
	flag.Parse()
	model, err := bookshop.Model(*demo)
	if err != nil {
		return fail(err)
	}
	summarizer, err := bookshop.SummarizerModel()
	if err != nil {
		return fail(err)
	}
	// A lower reply budget, such as 0.01, shows a budget stop; unset keeps the default.
	var budgets bookshop.Budgets
	if v := os.Getenv("BOOKSHOP_REPLY_BUDGET"); v != "" {
		if budgets.Reply, err = strconv.ParseFloat(v, 64); err != nil || budgets.Reply <= 0 {
			return fail(fmt.Errorf("BOOKSHOP_REPLY_BUDGET %q is not an amount of US dollars above zero", v))
		}
	}
	// Message text in traces is for debugging, so it is off unless asked for.
	var content bool
	if v := os.Getenv("BOOKSHOP_TELEMETRY_CONTENT"); v != "" {
		if content, err = strconv.ParseBool(v); err != nil {
			return fail(fmt.Errorf("BOOKSHOP_TELEMETRY_CONTENT %q is neither true nor false", v))
		}
	}
	// Input that is not a terminal is not shown as it is typed, so the console writes each line after its prompt.
	stdin, err := os.Stdin.Stat()
	echo := err == nil && stdin.Mode()&os.ModeCharDevice == 0
	app, err := bookshop.Build(ctx, bookshop.Config{
		Database: setting("BOOKSHOP_DATABASE", composeDatabase), Model: model, Summarizer: summarizer,
		In: os.Stdin, Out: os.Stdout, Echo: echo,
		// What the assistant remembers, per staff member, as the .NET application keeps it: data/memory.
		Memory: officina.NewFileMemoryStore(filepath.Join("data", "memory")),
		// Ctrl+C stops the reply in progress and the session goes on. Between replies nothing listens for it, so it
		// ends the application, as usual.
		Interrupt: func(ctx context.Context) (context.Context, context.CancelFunc) {
			return signal.NotifyContext(ctx, os.Interrupt)
		},
		TracerProvider: telemetry.Traces, MeterProvider: telemetry.Metrics, Logger: telemetry.Logger(),
		TelemetryContent: content,
		Dashboard:        setting("BOOKSHOP_DASHBOARD", composeDashboard), Budgets: budgets, Demo: *demo,
		Exports: setting("BOOKSHOP_EXPORTS", composeExports),
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

// setting returns the environment variable name, or fallback when it is empty.
func setting(name, fallback string) string {
	if v := os.Getenv(name); v != "" {
		return v
	}
	return fallback
}

// fail reports err and returns the exit code of a failure.
func fail(err error) int {
	fmt.Fprintln(os.Stderr, "bookshop:", err)
	return 1
}
