package main

import (
	"context"
	"fmt"
	"os"
	"os/signal"
	"sync"
	"time"

	"go.opentelemetry.io/otel"

	"github.com/sleepyshark85/officina/go/internal/bookshop"
)

// The compose file's services, in apps/BookshopAssistant: the database, with its demo password, and the telemetry
// dashboard, with its OTLP endpoint.
const (
	composeDatabase  = "postgres://bookshop:shelf-demo-41@localhost:5432/bookshop"
	composeDashboard = "http://localhost:18888"
	composeOTLP      = "http://localhost:4317"
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

	model, err := bookshop.Model()
	if err != nil {
		return fail(err)
	}
	// Input that is not a terminal is not shown as it is typed, so the console writes each line after its prompt.
	stdin, err := os.Stdin.Stat()
	echo := err == nil && stdin.Mode()&os.ModeCharDevice == 0
	app, err := bookshop.Build(ctx, bookshop.Config{
		Database: setting("BOOKSHOP_DATABASE", composeDatabase), Model: model, In: os.Stdin, Out: os.Stdout, Echo: echo,
		// Ctrl+C stops the reply in progress and the session goes on. Between replies nothing listens for it, so it
		// ends the application, as usual.
		Interrupt: func(ctx context.Context) (context.Context, context.CancelFunc) {
			return signal.NotifyContext(ctx, os.Interrupt)
		},
		TracerProvider: telemetry.Traces, MeterProvider: telemetry.Metrics, Logger: telemetry.Logger(),
		Dashboard: setting("BOOKSHOP_DASHBOARD", composeDashboard),
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
