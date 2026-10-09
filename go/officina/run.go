package officina

import (
	"context"
	"errors"
	"iter"
	"slices"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"go.opentelemetry.io/otel/attribute"
)

// RunOptions holds what one run gets besides its message, each optional. None of it is part of the prefix.
type RunOptions struct {
	// Context is the run context, appended after the user message as an operator message; empty for none.
	Context string
	// Budget limits the run; none by default. A host may give each run what is left of a session's budget.
	Budget Limits
}

// Result is how a run ended: Completed, Stopped or Failed, as Status says, always with what it used.
type Result struct {
	Status Status
	// Text is the final reply's text, when Completed.
	Text string
	// Output is the reply's typed output, a value of the type the agent's Output was made for, when Completed by an
	// agent with one; nil otherwise.
	Output any
	// Stop says why the run stopped, when Stopped.
	Stop StopReason
	// Failure says why the run failed, when Failed.
	Failure FailureReason
	// Detail is a refusal's category or the budget limit used up when Stopped, or what went wrong when Failed.
	Detail string
	Usage  Usage
	// Cost is what the run's tokens cost, in US dollars at the model's price; zero when the model has no price.
	Cost       float64
	ModelCalls int
	// ToolCalls counts the tool calls the run handled, denied and failed ones included.
	ToolCalls int
	Duration  time.Duration
}

// Status is how a run ended.
type Status int

// The ways a run ends; every run ends in exactly one.
const (
	Completed Status = iota + 1
	Stopped
	Failed
)

// StopReason is why a run stopped.
type StopReason int

// The reasons a run stops.
const (
	// Cancelled means the host cancelled the run, or stopped reading its events before it ended.
	Cancelled StopReason = iota + 1
	// Refusal means the model declined the request.
	Refusal
	// OutputLimit means the reply reached the output token limit.
	OutputLimit
	// ContextFull means the conversation no longer fits the model's context window.
	ContextFull
	// IterationLimit means the run made as many model calls as it may, and the model still asked for tools.
	IterationLimit
	// Budget means a limit of the run's budget was used up before a model call, or cut the reply short; the
	// result's detail says which.
	Budget
)

// FailureReason is why a run failed.
type FailureReason int

// The reasons a run fails.
const (
	// ModelError means the model call failed after retries, or its reply ended without a reason.
	ModelError FailureReason = iota + 1
	// UnexpectedStop means the model stopped for a reason the run cannot act on.
	UnexpectedStop
	// PrefixMismatch means the agent's prefix differs from the one the conversation was started with.
	PrefixMismatch
	// ToolSourceUnavailable means a tool source, such as an MCP server, could not connect at the start of the run.
	ToolSourceUnavailable
	// InvalidOutput means the reply is not the agent's typed output: it does not match the output schema, or does
	// not decode into the output type. It is not retried.
	InvalidOutput
)

// RunEvent is something that happened during a run, streamed to the host as it happens: a TextStreamed,
// ReplyRestarted, ConversationCompacted, ToolResultsCleared, UsageReported, ConversationAppended, ToolCallStarted,
// ApprovalAsked, ApprovalAnswered or ToolCallFinished.
type RunEvent interface {
	runEvent()
}

// TextStreamed is a piece of the model's reply text.
type TextStreamed struct {
	Text string
}

// ReplyRestarted says the model call failed after its reply had begun streaming and is made again: the text
// streamed since the last ConversationAppended is void, and the reply starts over.
type ReplyRestarted struct{}

// UsageReported is the tokens a model call reported since its previous report, and their cost in US dollars (zero
// when the model has no price).
type UsageReported struct {
	Usage Usage
	Cost  float64
}

// ConversationAppended reports a message appended to the conversation. The run waits while the host handles the
// event, so the host can save the conversation after every step.
type ConversationAppended struct {
	Message Message
}

func (TextStreamed) runEvent()         {}
func (ReplyRestarted) runEvent()       {}
func (UsageReported) runEvent()        {}
func (ConversationAppended) runEvent() {}

// ErrRunNotEnded is returned for a run's result asked for while its events are still being ranged over.
var ErrRunNotEnded = errors.New("run: result called before the events ended")

// Run runs the agent and returns its result; see Stream.
func (a *Agent) Run(ctx context.Context, c *Conversation, message string, opts RunOptions) (Result, error) {
	_, result := a.Stream(ctx, c, message, opts)
	return result()
}

// Stream runs the agent on conversation c with a user message, as events and a result. The run starts when the
// events are ranged over, and the result returns how it ended once they are done; called first, result runs it
// without reporting events. The events can be ranged over once. Called while they are still being ranged over,
// result returns ErrRunNotEnded.
//
// Before its first model call, the run connects the tool sources of the agent's tools, and fails with
// ToolSourceUnavailable if one cannot connect. While the model stops for tool calls, the run runs them, appends all
// their results as one message and calls the model again. The message and run context enter the conversation only with the model's reply, so a run that gets
// none leaves the conversation unchanged. Cancelling ctx, or stopping the range early, ends the run as
// Stopped(Cancelled) unless its result was already decided; a reply's calls still all get results, those that had
// not started a cancelled one. A nil c runs on a new conversation that is then discarded.
//
// The error is for the API misused: a blank message or run context, or a conversation another run is using
// (ErrConversationInUse); how the run went is the Result.
func (a *Agent) Stream(
	ctx context.Context, c *Conversation, message string, opts RunOptions,
) (events iter.Seq[RunEvent], result func() (Result, error)) {
	var (
		res          Result
		err          error
		ranged, done atomic.Bool
	)
	events = func(yield func(RunEvent) bool) {
		if ranged.Swap(true) {
			return
		}
		res, err = a.run(ctx, c, message, opts, yield)
		done.Store(true)
	}
	result = func() (Result, error) {
		events(func(RunEvent) bool { return true })
		if !done.Load() {
			return Result{}, ErrRunNotEnded
		}
		return res, err
	}
	return events, result
}

// maxModelCalls is the most model calls a run makes; one that needs more stops at IterationLimit.
const maxModelCalls = 25

// run makes one run, reporting its events to yield, and audits its start and end.
func (a *Agent) run(ctx context.Context, c *Conversation, message string, opts RunOptions, yield func(RunEvent) bool) (Result, error) {
	price := a.telemetry.model.Price
	switch {
	case strings.TrimSpace(message) == "":
		return Result{}, errors.New("run: blank message")
	case opts.Context != "" && strings.TrimSpace(opts.Context) == "":
		return Result{}, errors.New("run: blank run context")
	case opts.Budget.Cost != nil && price == Price{}:
		return Result{}, errors.New("run: a cost budget needs a model with a price")
	}
	if c == nil {
		c = &Conversation{}
	}
	if !c.running.CompareAndSwap(false, true) {
		return Result{}, ErrConversationInUse
	}
	defer c.running.Store(false)

	spent := &spending{budget: opts.Budget, price: price, started: time.Now()}
	ctx, span := a.telemetry.startRun(ctx, c.ID, message)
	audit := newRecorder(a, c.ID)
	span.SetAttributes(attribute.String("officina.run.id", audit.run))
	// A missing run entry blocks nothing: only a write's attempt depends on the trail.
	_ = audit.record(ctx, AuditEntry{Kind: AuditRunStarted})
	res := spent.report(a.loop(ctx, c, message, opts, audit, spent, yield))
	res.Text, res.Detail = a.redact(res.Text), a.redact(res.Detail)
	if res.Status == Completed && a.output != nil {
		var err error
		if res.Output, err = a.output.read(res.Text); err != nil {
			res.Status, res.Failure, res.Text, res.Detail = Failed, InvalidOutput, "", a.redact(err.Error())
		}
	}
	outcome := res.Status.String()
	switch res.Status {
	case Stopped:
		outcome += ": " + res.Stop.String()
	case Failed:
		outcome += ": " + res.Failure.String()
	}
	_ = audit.record(ctx, AuditEntry{ // As above.
		Kind: AuditRunEnded, Outcome: outcome, Detail: res.Detail, Usage: res.Usage, Cost: res.Cost,
	})
	a.telemetry.endRun(ctx, span, res)
	return res, nil
}

// CanContinue reports whether the agent's runs may go on with c: whether c is new, or was started by an agent with
// the same prefix (tools, instructions, model settings, output schema and context management), in this
// implementation or another. A run on a conversation it cannot continue fails with PrefixMismatch.
func (a *Agent) CanContinue(c *Conversation) bool {
	return c.fingerprint == "" || c.fingerprint == a.fingerprint
}

// loop calls the model and runs the tools it asks for until a reply ends the run. The result it returns holds
// neither usage nor counts: spent has them.
func (a *Agent) loop(ctx context.Context, c *Conversation, message string, opts RunOptions, audit *recorder,
	spent *spending, yield func(RunEvent) bool,
) Result {
	if !a.CanContinue(c) {
		return Result{Status: Failed, Failure: PrefixMismatch, Detail: "the agent's tools, instructions or model " +
			"settings differ from those the conversation was started with; start a new conversation"}
	}
	// Whatever the run started stops when it ends, the host's break included: once the host stops ranging, the run
	// reports nothing more and ends as cancelled, though a reply's calls still get their results.
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	if unavailable := a.connectSources(ctx, audit); unavailable != "" {
		return Result{Status: Failed, Failure: ToolSourceUnavailable, Detail: unavailable}
	}
	stopped := false
	emit := func(e RunEvent) {
		if !stopped && !yield(e) {
			stopped = true
			cancel()
		}
	}
	if answer := a.answerInterrupted(ctx, c, audit); answer != nil {
		emit(ConversationAppended{Message: *answer})
	}
	pending := []Message{textMessage(User, message)}
	if opts.Context != "" {
		pending = append(pending, textMessage(Operator, opts.Context))
	}
	for {
		reached := spent.reached()
		switch {
		case ctx.Err() != nil:
			return Result{Status: Stopped, Stop: Cancelled}
		case spent.modelCalls == maxModelCalls:
			return Result{Status: Stopped, Stop: IterationLimit}
		case reached != "":
			return Result{Status: Stopped, Stop: Budget, Detail: reached}
		}
		req := Request{
			Tools: a.tools, Instructions: a.instructions, ContextManagement: a.contextManagement,
			Messages: append(slices.Clip(c.messages), pending...),
		}
		if a.output != nil {
			req.OutputSchema = a.output.schema
		}
		limit, limited := spent.outputLimit()
		if limited {
			req.MaxOutputTokens = limit
		}
		r := a.call(ctx, req, emit)
		spent.add(r.usage)
		for _, e := range r.edits {
			// As for the run's own entries, a missing one blocks nothing.
			_ = audit.record(ctx, e)
		}
		blocks, finished := r.blocks, r.finished
		switch {
		case finished == nil && r.failure == nil:
			// Cut off mid-stream: nothing is appended, not even the message it would have answered.
			return Result{Status: Stopped, Stop: Cancelled}
		case finished == nil:
			return Result{Status: Failed, Failure: ModelError, Detail: r.failure.Error()}
		}

		end := finish(*finished, blocks)
		// A reply the lowered output limit cut short stopped for the budget, once the budget allows no more.
		if why := spent.reached(); limited && finished.Reason == FinishMaxTokens && why != "" {
			end = Result{Status: Stopped, Stop: Budget, Detail: why}
		}
		var toolCalls []ToolCall
		for _, b := range blocks {
			if b.ToolCall != nil {
				toolCalls = append(toolCalls, *b.ToolCall)
			}
		}
		switch {
		case len(blocks) == 0:
			return end
		// The provider rejects a call without its result, so a reply whose calls will not run is not kept.
		case toolCalls != nil && finished.Reason == FinishEnd:
			return Result{Status: Failed, Failure: UnexpectedStop, Detail: "the model's reply called tools but did " +
				"not stop for them"}
		case toolCalls != nil && finished.Reason != FinishToolUse:
			return end
		}

		// Everything is appended before any of it is reported, so a host that stops reading midway still holds a
		// conversation where the reply follows the messages it answers.
		c.fingerprint = a.fingerprint
		pending = append(pending, Message{Role: Assistant, Blocks: blocks})
		c.messages = append(c.messages, pending...)
		for _, m := range pending {
			emit(ConversationAppended{Message: m})
		}
		pending = nil
		if toolCalls == nil {
			return end
		}

		spent.toolCalls += len(toolCalls)
		results := a.runTools(ctx, toolCalls, audit, emit)
		a.recordSourceChanges(ctx, audit)
		answer := Message{Role: User, Blocks: make([]Block, len(results))}
		for i := range results {
			answer.Blocks[i] = Block{ToolResult: &results[i]}
		}
		c.messages = append(c.messages, answer)
		emit(ConversationAppended{Message: answer})
	}
}

// interrupted is the result of a call left without one when the application stopped while its tools ran.
const interrupted = "The call was interrupted: the application stopped before its result was recorded, so it may " +
	"or may not have taken effect."

// answerInterrupted gives an error result to each call of c's last message, when that is a reply whose calls have no
// results, as a crash while its tools ran leaves it: the provider rejects calls without results. It audits each and
// returns the message it appended, or nil when none was needed.
func (a *Agent) answerInterrupted(ctx context.Context, c *Conversation, audit *recorder) *Message {
	if len(c.messages) == 0 || c.messages[len(c.messages)-1].Role != Assistant {
		return nil
	}
	answer := Message{Role: User}
	for _, b := range c.messages[len(c.messages)-1].Blocks {
		if call := b.ToolCall; call != nil {
			// As for a run's own entries, a missing one blocks nothing: the call is not run again.
			_ = audit.record(ctx, AuditEntry{Kind: AuditToolEnded, Tool: call.Name, CallID: call.ID,
				Input: string(call.Input), Outcome: "interrupted", Detail: interrupted})
			answer.Blocks = append(answer.Blocks, Block{ToolResult: &ToolResult{CallID: call.ID, Content: interrupted,
				IsError: true}})
		}
	}
	if answer.Blocks == nil {
		return nil
	}
	c.messages = append(c.messages, answer)
	return &answer
}

// reply is what one model call returned.
type reply struct {
	blocks []Block
	// finished is the reply's finish; nil if it did not finish.
	finished *Finished
	usage    Usage
	// failure is why the call failed; nil if it finished or was cancelled.
	failure error
	retries int
	// edits are the audit entries of what the provider did to shorten the conversation.
	edits []AuditEntry
	// firstText is how long the call took to stream its first text, on the attempt that counts; negative if it
	// streamed none.
	firstText time.Duration
}

// errNoFinish is a reply that ended without saying why.
var errNoFinish = errors.New("the model's reply ended without a finish reason")

// call makes one model call, reporting its text and usage, and returns its reply.
func (a *Agent) call(ctx context.Context, req Request, emit func(RunEvent)) (r reply) {
	ctx, span := a.telemetry.startModelCall(ctx)
	started := time.Now()
	r.firstText = -1
	defer func() {
		switch {
		case r.finished != nil:
		case ctx.Err() != nil:
			r.failure = nil
		case r.failure == nil:
			r.failure = errNoFinish
		}
		a.telemetry.endModelCall(ctx, span, started, r)
	}()
	streamed := false
	for event, err := range a.model.Stream(ctx, req) {
		if err != nil || ctx.Err() != nil {
			r.failure = err
			return r
		}
		switch e := event.(type) {
		case TextDelta:
			if r.firstText < 0 {
				r.firstText = time.Since(started)
			}
			streamed = true
			emit(TextStreamed(e))
		case Retried:
			r.blocks, r.firstText = nil, -1
			r.retries++
			a.telemetry.retried(ctx)
			if streamed {
				streamed = false
				emit(ReplyRestarted{})
			}
		case BlockReceived:
			r.blocks = append(r.blocks, e.Block)
		case CompactionReported:
			a.telemetry.compacted(ctx, e)
			r.edits = append(r.edits, compactionEntry(e))
			emit(ConversationCompacted(e))
		case ClearingReported:
			a.telemetry.cleared(ctx, e)
			r.edits = append(r.edits, clearingEntry(e))
			emit(ToolResultsCleared(e))
		case UsageReceived:
			r.usage = r.usage.plus(e.Usage)
			emit(UsageReported{Usage: e.Usage, Cost: a.telemetry.model.Price.cost(e.Usage)})
		case Finished:
			r.finished = &e
			return r
		}
	}
	return r
}

// runTools runs a reply's tool calls in the pipeline, on a goroutine of its own, and reports their events here, on
// the run's goroutine, as they come. It returns once every call has its result.
func (a *Agent) runTools(ctx context.Context, calls []ToolCall, audit *recorder, emit func(RunEvent)) []ToolResult {
	// Sized for every event the calls can send, so the pipeline never waits for the host.
	events := make(chan RunEvent, len(calls)*eventsPerCall)
	var (
		results []ToolResult
		wg      sync.WaitGroup
	)
	wg.Go(func() {
		defer close(events)
		results = (&pipeline{agent: a, audit: audit, events: events}).run(ctx, calls)
	})
	for e := range events {
		emit(e)
	}
	wg.Wait()
	return results
}

// finish returns the result a reply's finish reason maps to.
func finish(f Finished, blocks []Block) Result {
	switch f.Reason {
	case FinishEnd:
		var text strings.Builder
		for _, b := range blocks {
			text.WriteString(b.Text)
		}
		return Result{Status: Completed, Text: text.String()}
	case FinishMaxTokens:
		return Result{Status: Stopped, Stop: OutputLimit}
	case FinishRefusal:
		return Result{Status: Stopped, Stop: Refusal, Detail: f.Detail}
	case FinishContextFull:
		return Result{Status: Stopped, Stop: ContextFull}
	case FinishToolUse:
		return Result{Status: Failed, Failure: UnexpectedStop, Detail: "the model stopped to use tools but called none"}
	default:
		return Result{Status: Failed, Failure: UnexpectedStop,
			Detail: "the model stopped for a reason the run cannot act on: " + f.Detail}
	}
}

// String returns the status's name.
func (s Status) String() string {
	return name(int(s), "Status", "Completed", "Stopped", "Failed")
}

// String returns the reason's name.
func (r StopReason) String() string {
	return name(int(r), "StopReason", "Cancelled", "Refusal", "OutputLimit", "ContextFull", "IterationLimit",
		"Budget")
}

// String returns the reason's name.
func (r FailureReason) String() string {
	return name(int(r), "FailureReason", "ModelError", "UnexpectedStop", "PrefixMismatch", "ToolSourceUnavailable",
		"InvalidOutput")
}

// name returns the name of the value v of an enumeration whose values start at 1, or the type and number for any
// other value, the zero value included.
func name(v int, typ string, names ...string) string {
	if v < 1 || v > len(names) {
		return typ + "(" + strconv.Itoa(v) + ")"
	}
	return names[v-1]
}
