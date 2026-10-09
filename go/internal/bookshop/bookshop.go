package bookshop

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"
	"go.opentelemetry.io/otel/metric"
	"go.opentelemetry.io/otel/trace"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

// Config is what Build wires: the boundaries of the application.
type Config struct {
	// Database is the PostgreSQL connection string, in either form pgx reads.
	Database string
	// Model is the chat agent's model: Claude (see Model) in the application, a scripted one in tests.
	Model officina.Model
	// In and Out are the staff member's console.
	In  io.Reader
	Out io.Writer
	// Echo writes each line read after its prompt, as a terminal would show it: for input that is not one.
	Echo bool
	// Interrupt returns the context of one reply, which is cancelled when the staff member interrupts the reply, and
	// a function that stops listening for that. Nil means nothing interrupts a reply.
	Interrupt func(ctx context.Context) (context.Context, context.CancelFunc)
	// TracerProvider and MeterProvider receive the traces and metrics of the core and the console; nil ones emit
	// none. Logger takes the application's logs; nil drops them.
	TracerProvider trace.TracerProvider
	MeterProvider  metric.MeterProvider
	Logger         *slog.Logger
	// Dashboard is the telemetry dashboard's address, which /audit links each run's trace to.
	Dashboard string
}

// App is Bookshop Assistant, wired: Run it, then Close it.
type App struct {
	db      *pgxpool.Pool
	agent   *officina.Agent
	console *console
}

// Build wires the application: the database pool, the bookshop tools, the audit table, the chat agent and the
// console, which is the agent's approver. The application starts with the database down, as the pool connects when a tool first needs it.
func Build(ctx context.Context, cfg Config) (*App, error) {
	if cfg.Model == nil || cfg.In == nil || cfg.Out == nil {
		return nil, errors.New("build bookshop: a model, an input and an output are required")
	}
	db, err := Connect(ctx, cfg.Database)
	if err != nil {
		return nil, fmt.Errorf("build bookshop: %w", err)
	}
	tools, err := Tools(db)
	if err != nil {
		db.Close()
		return nil, fmt.Errorf("build bookshop: %w", err)
	}
	trail := &auditTable{db: db}
	c := newConsole(cfg, trail)
	agent, err := officina.NewAgent(cfg.Model, instructions, officina.AgentOptions{
		Tools: tools, Approver: c, AuditSink: trail, Name: "bookshop",
		// The database password is a secret; the rest of the connection string is not.
		Secrets:        []string{db.Config().ConnConfig.Password},
		TracerProvider: cfg.TracerProvider, MeterProvider: cfg.MeterProvider,
	})
	if err != nil {
		db.Close()
		return nil, fmt.Errorf("build bookshop: %w", err)
	}
	return &App{db: db, agent: agent, console: c}, nil
}

// Connect returns a pool of connections to the database at url, a PostgreSQL connection string in either form pgx
// reads. It connects when a connection is first needed, so it succeeds with the database down.
func Connect(ctx context.Context, url string) (*pgxpool.Pool, error) {
	cfg, err := pgxpool.ParseConfig(url)
	if err != nil {
		return nil, fmt.Errorf("read the database's connection string: %w", err)
	}
	// Every connection taken from the pool is checked first, so once a stopped database is back, no call fails on
	// a connection it ended.
	cfg.ShouldPing = func(context.Context, pgxpool.ShouldPingParams) bool { return true }
	pool, err := pgxpool.NewWithConfig(ctx, cfg)
	if err != nil {
		return nil, fmt.Errorf("connect to the database: %w", err)
	}
	return pool, nil
}

// Run runs the console until the staff member quits or the input ends. Its error is the console's input or output
// failing; a reply that fails is shown, and the session goes on.
func (a *App) Run(ctx context.Context) error {
	return a.console.run(ctx, a.agent)
}

// Close closes the database pool.
func (a *App) Close() {
	a.db.Close()
}

// Model returns the chat agent's Claude model. Staff reply minutes apart and the prefix serves every session, so
// both caches last an hour. The API key comes from the environment (ANTHROPIC_API_KEY), as the SDK finds it.
func Model() (*claude.Model, error) {
	model, err := claude.New(claude.Opus55, claude.EffortMedium, claude.Options{
		MaxOutputTokens: 16_000, PrefixCache: claude.CacheOneHour, ConversationCache: claude.CacheOneHour,
	})
	if err != nil {
		return nil, fmt.Errorf("bookshop model: %w", err)
	}
	return model, nil
}

// runContext is the run context: today's date and who is at the counter.
func runContext(now time.Time, staffMember string) string {
	return "Today is " + now.Format("Monday 2 January 2006") + ". The staff member using the assistant is " +
		staffMember + "."
}

// instructions are frozen: who is at the counter and the date come as run context.
const instructions = `You are Bookshop Assistant, working alongside the staff of a small independent bookshop. Staff ask you, in
plain language, about the catalogue, the stock, customers and their orders, and ask you to make changes for
them. A message from the operator tells you today's date and which staff member you are talking to.

How to work:
- Use the tools for every fact about books, stock, customers and orders. Never guess an id, a price or a stock
  level; look it up. When a name could match several customers, ask which one is meant.
- Requests often take several steps. Do the lookups first (they may run together), then the change. Before a
  change, say in one short sentence what you are about to do.
- Changes (adding a customer, placing or cancelling an order, restocking) need the staff member's approval,
  which they give in their own interface. If they decline, accept it and offer an alternative.
- When a tool returns an error, read it. Business rule failures, such as too few copies in stock, mean nothing
  changed: explain and offer a way forward, such as fewer copies or another book. If the database cannot be
  reached, say so plainly and suggest trying again shortly; do not pretend the change was made.
- Prices are in pounds sterling. Give totals to the penny.

How to answer:
- Be brief and concrete: a few sentences, or a short list when there are several items. Name books by title and
  id, customers by name and id, orders by id.
- Do not show raw JSON or tool names to the staff member.`
