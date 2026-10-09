package bookshop

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"strings"
	"sync"
	"time"

	"go.opentelemetry.io/otel/trace"
	"go.opentelemetry.io/otel/trace/noop"

	"github.com/sleepyshark85/officina/go/officina"
)

const help = `Commands:
  /help          Show this help.
  /new           Start a new session.
  /sessions      List the latest sessions.
  /resume <id>   Go on with the session with that id.
  /cost          Show this session's tokens and cost.
  /audit [<id>]  Show the audit trail of this session, or of the session with that id.
  /quit          Leave the assistant.
Anything else is a message to the assistant. Ctrl+C stops a reply in progress.`

// console is the staff member's console: it asks who is using it, reads messages and commands, streams each reply
// with its tool activity, asks approval for changes and stops a reply when interrupted.
//
// Each conversation is a session, saved after every step of a reply; each reply ends with a status line of its
// tokens and cost, and stops at its own or its session's budget.
//
// Each reply is a span, the parent of the run's trace, so the console's logs join it; /audit reads the audit table
// and links each run to its trace on the dashboard.
//
// It is also the agent's approver. The run reports each approval request as an event, which the console handles
// on the run's goroutine, in order with everything shown before it: it asks the staff member and hands the answer
// to Approve, which the run calls on its tool pipeline's goroutine and which waits for it.
type console struct {
	in        *bufio.Reader
	out       io.Writer
	echo      bool
	interrupt func(ctx context.Context) (context.Context, context.CancelFunc)
	tracer    trace.Tracer
	logger    *slog.Logger
	trail     *auditTable
	sessions  *Sessions
	budgets   Budgets
	dashboard string
	// answers hands one approval from the console to Approve; one reply asks one approval at a time.
	answers chan officina.Approval
	demo    bool

	// pending holds the line being read, while a read is in flight; a read an interrupt abandoned serves the next
	// prompt. reads waits for the reading goroutine.
	pending chan read
	reads   sync.WaitGroup

	atLineStart bool
	// err is the first output error; once there is one, nothing more is written.
	err error
}

// read is a line read, or the error that ended the input.
type read struct {
	text string
	err  error
}

func newConsole(cfg Config, trail *auditTable, sessions *Sessions) *console {
	c := &console{
		in: bufio.NewReader(cfg.In), out: cfg.Out, echo: cfg.Echo, interrupt: cfg.Interrupt, logger: cfg.Logger,
		trail: trail, sessions: sessions, budgets: cfg.Budgets, dashboard: cfg.Dashboard,
		answers: make(chan officina.Approval, 1), atLineStart: true, demo: cfg.Demo,
	}
	if c.budgets.Reply == 0 {
		c.budgets.Reply = defaultReplyBudget
	}
	if c.budgets.Session == 0 {
		c.budgets.Session = defaultSessionBudget
	}
	if c.interrupt == nil {
		c.interrupt = context.WithCancel
	}
	tp := cfg.TracerProvider
	if tp == nil {
		tp = noop.NewTracerProvider()
	}
	c.tracer = tp.Tracer(scope)
	if c.logger == nil {
		c.logger = slog.New(slog.DiscardHandler)
	}
	return c
}

// run runs the session until /quit or the end of the input.
func (c *console) run(ctx context.Context, agent *officina.Agent) error {
	// A line still being read is waited for; run returns only after the read it asked for has ended.
	defer c.reads.Wait()
	c.writeLine("Bookshop Assistant. Type /help for commands.")
	if c.demo {
		c.writeLine("Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 tool calls.")
	}
	staffMember, err := c.askStaffMember(ctx)
	if err != nil {
		return c.ended(err)
	}
	s := newSession(staffMember)
	c.writeLine("Session " + s.conversation.ID + ".")
	for {
		text, err := c.read(ctx, "you> ")
		if err != nil {
			return c.ended(err)
		}
		command := strings.TrimSpace(text)
		resume, isResume := argument(command, "/resume")
		audit, isAudit := argument(command, "/audit")
		switch {
		case command == "":
			continue
		case command == "/quit":
			return c.err
		case command == "/help":
			c.writeLine(help)
			continue
		case command == "/new":
			s = newSession(staffMember)
			c.writeLine("New session " + s.conversation.ID + ".")
			continue
		case command == "/sessions":
			c.listSessions(ctx, s)
			continue
		case command == "/cost":
			c.writeLine(fmt.Sprintf("Session %s: %s; cost $%.4f of its $%.2f budget.", s.conversation.ID,
				tokens(s.usage), s.cost, c.budgets.Session))
			continue
		case isResume:
			s = c.resume(ctx, agent, resume, s)
			continue
		case isAudit:
			if audit == "" {
				audit = s.conversation.ID
			}
			c.showAudit(ctx, audit)
			continue
		case strings.HasPrefix(command, "/"):
			c.writeLine("Unknown command " + command + ". Type /help for commands.")
			continue
		}
		// The run context is sent at the start of the session and again only when it changes: on a new day.
		current := runContext(time.Now(), staffMember)
		if current == s.context {
			current = ""
		}
		if err := c.reply(ctx, agent, s, text, current); err != nil {
			return err
		}
	}
}

// argument returns what follows the command name in command, trimmed, and whether command is that command.
func argument(command, name string) (string, bool) {
	if command != name && !strings.HasPrefix(command, name+" ") {
		return "", false
	}
	return strings.TrimSpace(command[len(name):]), true
}

// ended returns the error that ends a session at err, a read's: none at the end of the input, else the first of
// the output's error and err.
func (c *console) ended(err error) error {
	switch {
	case c.err != nil:
		return c.err
	case errors.Is(err, io.EOF):
		return nil
	default:
		return err
	}
}

func (c *console) askStaffMember(ctx context.Context) (string, error) {
	for {
		name, err := c.read(ctx, "Who is using the assistant? Your name: ")
		if err != nil {
			return "", err
		}
		if name = strings.TrimSpace(name); name != "" {
			c.writeLine("Hello, " + name + ".")
			return name, nil
		}
	}
}

// showAudit shows the audit trail of session.
func (c *console) showAudit(ctx context.Context, session string) {
	entries, err := c.trail.entries(ctx, session)
	if err != nil {
		c.writeLine("The audit trail could not be read: " + err.Error())
		return
	}
	c.writeLine(formatAudit(session, entries, c.dashboard, time.Local))
}

// reply streams one reply to message in session s, with the run context newContext if it is not empty, saving the
// session after every step, within the lower of the reply's budget and what is left of the session's. Its error is
// the API misused, which is a bug.
func (c *console) reply(ctx context.Context, agent *officina.Agent, s *session, message, newContext string) error {
	ctx, span := c.tracer.Start(ctx, "reply")
	defer span.End()
	ctx, stop := c.interrupt(ctx)
	defer stop()
	left := c.budgets.Session - s.cost
	budget := max(0, min(c.budgets.Reply, left))
	events, result := agent.Stream(ctx, s.conversation, message, officina.RunOptions{
		Context: newContext, Budget: officina.Limits{Cost: &budget},
	})
	labelled, saveFailed, compacted := false, false, false
	// What the reply has spent so far, from each model call's usage, so every save stores the session's whole spend.
	var (
		spent     officina.Usage
		spentCost float64
	)
	for event := range events {
		switch e := event.(type) {
		case officina.TextStreamed:
			if !labelled {
				c.endLine()
				c.write("assistant> ")
				labelled = true
			}
			c.write(e.Text)
		case officina.ReplyRestarted:
			c.writeLine("[The reply was interrupted and starts again.]")
		case officina.ToolCallStarted:
			c.writeLine("  > " + e.Call.Name + " " + string(e.Call.Input))
		case officina.ApprovalAsked:
			c.askApproval(ctx, e.Call)
		case officina.ToolCallFinished:
			if e.Result.IsError {
				first, _, _ := strings.Cut(e.Result.Content, "\n")
				c.writeLine("  < " + e.Call.Name + ": error: " + first)
			} else {
				c.writeLine("  < " + e.Call.Name + ": ok")
			}
		case officina.ConversationCompacted:
			compacted = true
			c.writeLine("  ~ Conversation compacted: " + thousands(e.Tokens) + " tokens summarized into " +
				thousands(e.SummaryTokens) + ".")
		case officina.ToolResultsCleared:
			// The provider clears again on every call, as each sends the whole conversation: a line shows a change.
			if e.ToolCalls != s.cleared {
				s.cleared = e.ToolCalls
				c.writeLine(fmt.Sprintf("  ~ Old tool results cleared: %d tool calls, %s tokens.", e.ToolCalls,
					thousands(e.Tokens)))
			}
		case officina.UsageReported:
			spent, spentCost = plus(spent, e.Usage), spentCost+e.Cost
		case officina.ConversationAppended:
			if e.Message.Role == officina.Operator {
				s.context = e.Message.Text()
			}
			saveFailed = !c.save(ctx, s, plus(s.usage, spent), s.cost+spentCost, saveFailed) || saveFailed
		}
	}
	// The run has ended, so nothing waits for an answer: drop one a cancelled run left.
	select {
	case <-c.answers:
	default:
	}
	res, err := result()
	if err != nil {
		return fmt.Errorf("reply: %w", err)
	}
	s.usage, s.cost = plus(s.usage, res.Usage), s.cost+res.Cost
	c.save(ctx, s, s.usage, s.cost, saveFailed)
	u := res.Usage
	c.logger.InfoContext(ctx, "reply ended", "conversation", s.conversation.ID, "result", res.Status.String(),
		"input_tokens", u.Input+u.CacheRead+u.CacheWrite, "output_tokens", u.Output)
	switch {
	case res.Status == officina.Completed && strings.TrimSpace(res.Text) == "" && compacted:
		c.writeLine("[The conversation was compacted and the reply has no text. Please ask again.]")
	case res.Status == officina.Stopped && res.Stop == officina.Cancelled:
		c.writeLine("[Cancelled.]")
	case res.Status == officina.Stopped && res.Stop == officina.Budget && left <= c.budgets.Reply:
		c.writeLine("[Stopped: this session has reached its budget of $" + dollars(c.budgets.Session) +
			". Type /new to start a new session.]")
	case res.Status == officina.Stopped && res.Stop == officina.Budget:
		c.writeLine("[Stopped: this reply has reached its budget of $" + dollars(c.budgets.Reply) + ".]")
	case res.Status == officina.Stopped:
		c.writeLine("[Stopped: " + res.Stop.String() + ".]")
	case res.Status == officina.Failed && res.Failure == officina.PrefixMismatch:
		c.writeLine("[This session was started with another version of the assistant, so it cannot go on. " +
			"Type /new to start a new session.]")
	case res.Status == officina.Failed:
		c.logger.ErrorContext(ctx, "reply failed", "conversation", s.conversation.ID, "reason",
			res.Failure.String(), "error", res.Detail)
		c.writeLine("[Failed: " + res.Detail + "]")
	}
	c.writeLine(fmt.Sprintf("[%s · reply $%.4f · session $%.4f]", tokens(res.Usage), res.Cost, s.cost))
	return nil
}

// askApproval shows the call's exact input and asks the staff member; the waiting Approve gets the answer. An
// interrupt at the prompt takes effect at once: the run stops waiting, and the line being typed is the next message.
func (c *console) askApproval(ctx context.Context, call officina.ToolCall) {
	if ctx.Err() != nil {
		return
	}
	c.writeLine("  ? " + call.Name + " needs your approval. Its exact input:")
	c.writeLine("    " + string(call.Input))
	answer, err := c.read(ctx, "    Approve? [y/N] ")
	approval := officina.Approval{Reason: "the staff member declined"}
	switch a := strings.ToLower(strings.TrimSpace(answer)); {
	case ctx.Err() != nil:
		// Approve has stopped waiting, so no answer goes to it.
		return
	case err == nil && (a == "y" || a == "yes"):
		approval = officina.Approval{Approved: true}
	}
	c.answers <- approval
}

// Approve waits for the staff member's answer, which the console asks for on the call's approval event.
func (c *console) Approve(ctx context.Context, _ officina.Tool, _ officina.ToolCall) (officina.Approval, error) {
	select {
	case approval := <-c.answers:
		return approval, nil
	case <-ctx.Done():
		return officina.Approval{}, fmt.Errorf("wait for the staff member: %w", ctx.Err())
	}
}

// read shows prompt and reads a line, without its line ending. It returns io.EOF at the end of the input, and
// ctx's error if ctx ends first; the line then serves the next read.
func (c *console) read(ctx context.Context, prompt string) (string, error) {
	c.endLine()
	c.write(prompt)
	if ctx.Err() != nil {
		return "", fmt.Errorf("read: %w", ctx.Err())
	}
	// A blocked read cannot be stopped, so it runs aside, and one an interrupt abandons serves the next prompt.
	if c.pending == nil {
		pending := make(chan read, 1)
		c.reads.Go(func() {
			text, err := c.in.ReadString('\n')
			if err != nil && text != "" {
				err = nil // The last line has no line ending; the end of the input comes with the next read.
			}
			pending <- read{strings.TrimRight(text, "\r\n"), err}
		})
		c.pending = pending
	}
	select {
	case r := <-c.pending:
		// The line can arrive just as ctx ends: it then serves the next read.
		if ctx.Err() != nil {
			c.pending <- r
			return "", fmt.Errorf("read: %w", ctx.Err())
		}
		c.pending = nil
		if r.err != nil {
			c.endLine()
			return "", r.err
		}
		if c.echo {
			c.write(r.text + "\n")
		}
		c.atLineStart = true
		return r.text, nil
	case <-ctx.Done():
		return "", fmt.Errorf("read: %w", ctx.Err())
	}
}

// write writes text unless an earlier write failed.
func (c *console) write(text string) {
	if text == "" || c.err != nil {
		return
	}
	if _, err := io.WriteString(c.out, text); err != nil {
		c.err = fmt.Errorf("write to the console: %w", err)
		return
	}
	c.atLineStart = strings.HasSuffix(text, "\n")
}

// writeLine writes text as a line of its own; an empty text only ends the current line.
func (c *console) writeLine(text string) {
	c.endLine()
	if text != "" {
		c.write(text + "\n")
	}
}

// endLine ends the current line, unless it is empty.
func (c *console) endLine() {
	if !c.atLineStart {
		c.write("\n")
	}
}
