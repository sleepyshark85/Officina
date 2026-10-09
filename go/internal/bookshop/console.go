package bookshop

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/hex"
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
  /audit [<id>]  Show the audit trail of this session, or of the session with that id.
  /quit          Leave the assistant.
Anything else is a message to the assistant. Ctrl+C stops a reply in progress.`

// console is the staff member's console: it asks who is using it, reads messages and commands, streams each reply
// with its tool activity, asks approval for changes and stops a reply when interrupted.
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
	dashboard string
	// answers hands one approval from the console to Approve; one reply asks one approval at a time.
	answers chan officina.Approval

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

func newConsole(cfg Config, trail *auditTable) *console {
	c := &console{
		in: bufio.NewReader(cfg.In), out: cfg.Out, echo: cfg.Echo, interrupt: cfg.Interrupt, logger: cfg.Logger,
		trail: trail, dashboard: cfg.Dashboard, answers: make(chan officina.Approval, 1), atLineStart: true,
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
	staffMember, err := c.askStaffMember(ctx)
	if err != nil {
		return c.ended(err)
	}
	var (
		// A session's id is its conversation's, which the audit trail names.
		conversation = officina.Conversation{ID: newSessionID()}
		// sent is the run context the conversation last received.
		sent string
	)
	for {
		text, err := c.read(ctx, "you> ")
		if err != nil {
			return c.ended(err)
		}
		switch command := strings.TrimSpace(text); {
		case command == "":
			continue
		case command == "/quit":
			return c.err
		case command == "/help":
			c.writeLine(help)
			continue
		case command == "/audit" || strings.HasPrefix(command, "/audit "):
			session := strings.TrimSpace(strings.TrimPrefix(command, "/audit"))
			if session == "" {
				session = conversation.ID
			}
			c.showAudit(ctx, session)
			continue
		case strings.HasPrefix(command, "/"):
			c.writeLine("Unknown command " + command + ". Type /help for commands.")
			continue
		}
		// The run context is sent at the start of the session and again only when it changes: on a new day.
		current := runContext(time.Now(), staffMember)
		if current == sent {
			current = ""
		}
		received, err := c.reply(ctx, agent, &conversation, text, current)
		if err != nil {
			return err
		}
		if received != "" {
			sent = received
		}
	}
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

// newSessionID returns a new session id: 32 random hex digits, as the .NET implementation makes them.
func newSessionID() string {
	id := make([]byte, 16)
	_, _ = rand.Read(id) // It never fails: it crashes the program instead.
	return hex.EncodeToString(id)
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

// reply streams one reply to message, with the run context newContext if it is not empty, and returns the run
// context the conversation received, if it received one. Its error is the API misused, which is a bug.
func (c *console) reply(ctx context.Context, agent *officina.Agent, conversation *officina.Conversation,
	message, newContext string,
) (received string, err error) {
	ctx, span := c.tracer.Start(ctx, "reply")
	defer span.End()
	ctx, stop := c.interrupt(ctx)
	defer stop()
	events, result := agent.Stream(ctx, conversation, message, officina.RunOptions{Context: newContext})
	labelled := false
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
		case officina.ConversationAppended:
			if e.Message.Role == officina.Operator {
				received = e.Message.Text()
			}
		}
	}
	// The run has ended, so nothing waits for an answer: drop one a cancelled run left.
	select {
	case <-c.answers:
	default:
	}
	res, err := result()
	if err != nil {
		return "", fmt.Errorf("reply: %w", err)
	}
	u := res.Usage
	c.logger.InfoContext(ctx, "reply ended", "conversation", conversation.ID, "result", res.Status.String(),
		"input_tokens", u.Input+u.CacheRead+u.CacheWrite, "output_tokens", u.Output)
	switch res.Status {
	case officina.Completed:
	case officina.Stopped:
		if res.Stop == officina.Cancelled {
			c.writeLine("[Cancelled.]")
		} else {
			c.writeLine("[Stopped: " + res.Stop.String() + ".]")
		}
	case officina.Failed:
		c.logger.ErrorContext(ctx, "reply failed", "conversation", conversation.ID, "reason", res.Failure.String(),
			"error", res.Detail)
		c.writeLine("[Failed: " + res.Detail + "]")
	}
	c.endLine()
	return received, nil
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
