package officina

import (
	"context"
	"encoding/json/v2"
	"fmt"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"go.opentelemetry.io/otel/trace"
)

// ToolCallStarted says the run began handling a tool call: its approval, if it needs one, then the tool.
type ToolCallStarted struct {
	// Call is the call with the agent's secrets redacted from its input.
	Call ToolCall
}

// ApprovalAsked says the run asks the approver about a call. It may reach the host before or after the approver is
// asked.
type ApprovalAsked struct {
	Call ToolCall
}

// ApprovalAnswered says the approver answered, or failed to, which denies the call.
type ApprovalAnswered struct {
	Call     ToolCall
	Approved bool
}

// ToolCallFinished says a call got its result, as the model will see it; every call gets one, whether it ran or not.
type ToolCallFinished struct {
	Call   ToolCall
	Result ToolResult
}

func (ToolCallStarted) runEvent()  {}
func (ApprovalAsked) runEvent()    {}
func (ApprovalAnswered) runEvent() {}
func (ToolCallFinished) runEvent() {}

const (
	// maxResult is the longest result the model gets, in bytes: about 16k tokens.
	maxResult = 64_000
	// eventsPerCall bounds the events one call sends: started, approval asked and answered, finished.
	eventsPerCall = 4
)

// pipeline runs one reply's tool calls: find the tool, validate the input, ask approval if needed, record the
// attempt, invoke, truncate. Reads run concurrently; a write waits for the calls before it and runs alone; approvals
// are asked one at a time, in call order. Every call gets exactly one result, and every failure is an error result.
type pipeline struct {
	agent  *Agent
	audit  *recorder
	events chan<- RunEvent
}

// step is a call the pipeline started: its span and when it started.
type step struct {
	span    trace.Span
	started time.Time
}

// notRun is the time a tool ran when it did not run.
const notRun time.Duration = -1

// run returns the calls' results, in call order. Once ctx is done, no further call starts; those get a cancelled
// error result.
func (p *pipeline) run(ctx context.Context, calls []ToolCall) []ToolResult {
	results := make([]*ToolResult, len(calls))
	// steps holds the calls started, so one cancelled before its tool ran still ends its span.
	steps := make([]*step, len(calls))
	var reads sync.WaitGroup
	for i, call := range calls {
		if ctx.Err() != nil {
			break
		}
		t, found := p.agent.tool(call.Name)
		if found && t.Kind == Write {
			reads.Wait()
		}
		ctx, span := p.agent.telemetry.startToolCall(ctx, t, found, call)
		s := &step{span: span, started: time.Now()}
		steps[i] = s
		p.events <- ToolCallStarted{Call: p.shown(call)}
		rejected := p.prepare(ctx, t, found, call)
		if rejected != "" {
			results[i] = p.end(ctx, s, call, rejected, toolError, notRun)
			continue
		}
		// The host may have cancelled while the approver decided, or while this write waited for the reads.
		if ctx.Err() != nil {
			break
		}
		err := p.audit.record(ctx, AuditEntry{Kind: AuditToolStarted, Tool: t.Name, CallID: call.ID, Input: string(call.Input)})
		if err != nil && t.Kind == Write {
			results[i] = p.end(ctx, s, call, "The call was not run: its attempt could not be recorded in the audit trail.",
				toolBlocked, notRun)
			continue
		}
		if t.Kind == Write {
			results[i] = p.invoke(ctx, s, t, call)
		} else {
			reads.Go(func() { results[i] = p.invoke(ctx, s, t, call) })
		}
	}
	reads.Wait()

	answered := make([]ToolResult, len(calls))
	for i, r := range results {
		if r == nil {
			r = p.end(ctx, steps[i], calls[i], "The call was cancelled before it started.", toolError, notRun)
		}
		answered[i] = *r
	}
	return answered
}

// prepare validates a call and asks for its approval; it returns why the call may not run, or "" when it may.
func (p *pipeline) prepare(ctx context.Context, t tool, found bool, call ToolCall) string {
	var input any
	if !found {
		return fmt.Sprintf("There is no tool named %q.", call.Name)
	}
	if err := json.Unmarshal(call.Input, &input); err != nil {
		return "The input is not valid JSON: " + err.Error()
	}
	if problems := t.schema.validate(input); problems != nil {
		return "The input does not match the tool's schema:\n" + strings.Join(problems, "\n")
	}
	// The memory tool's views only read, so they never need approval.
	if !t.NeedsApproval || t.memory && input.(map[string]any)["command"] == "view" {
		return ""
	}
	approver := p.agent.approver
	if approver == nil {
		return "The call needs approval, and this run is unattended, so it was denied."
	}
	// The trail records the question even when it cannot: only a write's attempt depends on the sink.
	_ = p.audit.record(ctx, AuditEntry{Kind: AuditApprovalAsked, Tool: t.Name, CallID: call.ID, Input: string(call.Input)})
	p.events <- ApprovalAsked{Call: p.shown(call)}
	asked := time.Now()
	approval, err := ask(ctx, approver, t.Tool, call)
	switch {
	case err != nil && ctx.Err() != nil:
		// A cancelled run gave the approver no time to answer: only the wait is told.
		p.agent.telemetry.waited(ctx, time.Since(asked))
		approval = Approval{Reason: "the run was cancelled while waiting for approval"}
	case err != nil:
		approval = Approval{Reason: err.Error()}
		p.agent.telemetry.approved(ctx, t.Name, false, time.Since(asked))
	default:
		p.agent.telemetry.approved(ctx, t.Name, approval.Approved, time.Since(asked))
	}
	outcome := "denied"
	if approval.Approved {
		outcome = "approved"
	}
	_ = p.audit.record(ctx, AuditEntry{ // As above.
		Kind: AuditApprovalAnswered, Tool: t.Name, CallID: call.ID, Outcome: outcome, Detail: approval.Reason,
	})
	p.events <- ApprovalAnswered{Call: p.shown(call), Approved: approval.Approved}
	switch {
	case approval.Approved:
		return ""
	case approval.Reason == "":
		return "The call was denied."
	default:
		return "The call was denied: " + approval.Reason
	}
}

// ask asks approver about call; a panic in the approver is an error, as the host cannot recover it on the
// pipeline's goroutine.
func ask(ctx context.Context, approver Approver, t Tool, call ToolCall) (approval Approval, err error) {
	defer func() {
		if r := recover(); r != nil {
			approval, err = Approval{}, fmt.Errorf("asking for approval failed: the approver panicked: %v", r)
		}
	}()
	if approval, err = approver.Approve(ctx, t, call); err != nil {
		return Approval{}, fmt.Errorf("asking for approval failed: %w", err)
	}
	return approval, nil
}

// invoke runs the tool on the call's input, already validated, and returns its result; a panic in the handler is an
// error result.
func (p *pipeline) invoke(ctx context.Context, s *step, t tool, call ToolCall) *ToolResult {
	started := time.Now()
	content, failed := func() (content string, failed bool) {
		defer func() {
			if r := recover(); r != nil {
				content, failed = fmt.Sprintf("The tool failed: %v", r), true
			}
		}()
		content, err := t.Handler(ctx, call.Input)
		switch {
		case err != nil && ctx.Err() != nil:
			return "The call was cancelled while it ran: " + err.Error(), true
		case err != nil:
			return err.Error(), true
		}
		return content, false
	}()
	outcome := toolOK
	if failed {
		outcome = toolError
	}
	return p.end(ctx, s, call, content, outcome, time.Since(started))
}

// end redacts and truncates a call's result, records its outcome, ends its step, if it started, and reports it. The
// tool ran for ran, or not at all if ran is notRun.
func (p *pipeline) end(ctx context.Context, s *step, call ToolCall, content string, outcome toolOutcome,
	ran time.Duration,
) *ToolResult {
	if s != nil {
		ctx = trace.ContextWithSpan(ctx, s.span)
	}
	content = p.agent.redact(content)
	length := len(content)
	if length > maxResult {
		content = cut(content, maxResult) + "\n[Truncated: the result had " + strconv.Itoa(length) +
			" bytes; only the first " + strconv.Itoa(maxResult) + " are shown.]"
	}
	failed := outcome != toolOK
	audited := "ok"
	if failed {
		audited = "error"
	}
	// A missing outcome entry blocks nothing: the call is over.
	_ = p.audit.record(ctx, AuditEntry{
		Kind: AuditToolEnded, Tool: call.Name, CallID: call.ID, Input: string(call.Input), Outcome: audited, Detail: content,
		Duration: max(ran, 0),
	})
	if s != nil {
		p.agent.telemetry.endToolCall(ctx, s.span, s.started, call, outcome, ran, length, content)
	}
	result := ToolResult{CallID: call.ID, Content: content, IsError: failed}
	p.events <- ToolCallFinished{Call: p.shown(call), Result: result}
	return &result
}

// shown returns the call as events show it: its input without the agent's secrets.
func (p *pipeline) shown(call ToolCall) ToolCall {
	call.Input = []byte(p.agent.redact(string(call.Input)))
	return call
}

// cut returns s cut to at most n bytes, never inside a character.
func cut(s string, n int) string {
	for n > 0 && !utf8.RuneStart(s[n]) {
		n--
	}
	return s[:n]
}
