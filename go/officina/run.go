package officina

import (
	"context"
	"errors"
	"iter"
	"slices"
	"strconv"
	"strings"
	"sync/atomic"
)

// RunOptions holds what one run gets besides its message, each optional. None of it is part of the prefix.
type RunOptions struct {
	// Context is the run context, appended after the user message as an operator message; empty for none.
	Context string
}

// Result is how a run ended: Completed, Stopped or Failed, as Status says, always with the tokens used.
type Result struct {
	Status Status
	// Text is the final reply's text, when Completed.
	Text string
	// Stop says why the run stopped, when Stopped.
	Stop StopReason
	// Failure says why the run failed, when Failed.
	Failure FailureReason
	// Detail is a refusal's category when Stopped, or what went wrong when Failed.
	Detail string
	Usage  Usage
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
)

// RunEvent is something that happened during a run, streamed to the host as it happens: a TextStreamed,
// UsageReported or ConversationAppended.
type RunEvent interface {
	runEvent()
}

// TextStreamed is a piece of the model's reply text.
type TextStreamed struct {
	Text string
}

// UsageReported is the tokens a model call reported since its previous report.
type UsageReported struct {
	Usage Usage
}

// ConversationAppended reports a message appended to the conversation. The run waits while the host handles the
// event, so the host can save the conversation after every step.
type ConversationAppended struct {
	Message Message
}

func (TextStreamed) runEvent()         {}
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
// The message and run context enter the conversation only with the model's reply, so a run that gets none leaves
// the conversation unchanged. Cancelling ctx, or stopping the range early, ends the run as Stopped(Cancelled) unless
// its result was already decided. A nil c runs on a new conversation that is then discarded.
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

// run makes one run, reporting its events to yield.
func (a *Agent) run(ctx context.Context, c *Conversation, message string, opts RunOptions, yield func(RunEvent) bool) (Result, error) {
	if strings.TrimSpace(message) == "" {
		return Result{}, errors.New("run: blank message")
	}
	if opts.Context != "" && strings.TrimSpace(opts.Context) == "" {
		return Result{}, errors.New("run: blank run context")
	}
	if c == nil {
		c = &Conversation{}
	}
	if !c.running.CompareAndSwap(false, true) {
		return Result{}, ErrConversationInUse
	}
	defer c.running.Store(false)

	if c.fingerprint != "" && c.fingerprint != a.fingerprint {
		return Result{Status: Failed, Failure: PrefixMismatch, Detail: "the agent's tools, instructions or model " +
			"settings differ from those the conversation was started with; start a new conversation"}, nil
	}
	// Whatever the model call started stops when the run ends, the host's break included.
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	cancelled := Result{Status: Stopped, Stop: Cancelled}
	if ctx.Err() != nil {
		return cancelled, nil
	}
	pending := []Message{textMessage(User, message)}
	if opts.Context != "" {
		pending = append(pending, textMessage(Operator, opts.Context))
	}
	req := Request{Tools: a.tools, Instructions: a.instructions, Messages: append(slices.Clip(c.messages), pending...)}

	var (
		blocks   []Block
		finished *Finished
		failure  error
	)
	for event, err := range a.model.Stream(ctx, req) {
		if err != nil || ctx.Err() != nil {
			failure = err
			break
		}
		switch e := event.(type) {
		case TextDelta:
			if !yield(TextStreamed(e)) {
				return cancelled, nil
			}
		case BlockReceived:
			blocks = append(blocks, e.Block)
		case UsageReceived:
			cancelled.Usage = cancelled.Usage.plus(e.Usage)
			if !yield(UsageReported(e)) {
				return cancelled, nil
			}
		case Finished:
			finished = &e
		}
		if finished != nil {
			break
		}
	}
	usage := cancelled.Usage
	switch {
	case finished == nil && ctx.Err() != nil:
		// Cut off mid-stream: nothing is appended, not even the message it would have answered.
		return cancelled, nil
	case finished == nil && failure != nil:
		return Result{Status: Failed, Failure: ModelError, Detail: failure.Error(), Usage: usage}, nil
	case finished == nil:
		return Result{Status: Failed, Failure: ModelError, Detail: "the model's reply ended without a finish reason",
			Usage: usage}, nil
	}

	end := finish(*finished, blocks, usage)
	if len(blocks) == 0 {
		return end, nil
	}
	// Everything is appended before any of it is reported, so a host that stops reading midway still holds a
	// conversation where the reply follows the messages it answers.
	c.fingerprint = a.fingerprint
	pending = append(pending, Message{Role: Assistant, Blocks: blocks})
	c.messages = append(c.messages, pending...)
	for _, m := range pending {
		if !yield(ConversationAppended{Message: m}) {
			break
		}
	}
	return end, nil
}

// finish returns the result a reply's finish reason maps to.
func finish(f Finished, blocks []Block, usage Usage) Result {
	switch f.Reason {
	case FinishEnd:
		var text strings.Builder
		for _, b := range blocks {
			text.WriteString(b.Text)
		}
		return Result{Status: Completed, Text: text.String(), Usage: usage}
	case FinishMaxTokens:
		return Result{Status: Stopped, Stop: OutputLimit, Usage: usage}
	case FinishRefusal:
		return Result{Status: Stopped, Stop: Refusal, Detail: f.Detail, Usage: usage}
	case FinishContextFull:
		return Result{Status: Stopped, Stop: ContextFull, Usage: usage}
	case FinishToolUse:
		return Result{Status: Failed, Failure: UnexpectedStop, Detail: "the model stopped to use tools but called none",
			Usage: usage}
	default:
		return Result{Status: Failed, Failure: UnexpectedStop,
			Detail: "the model stopped for a reason the run cannot act on: " + f.Detail, Usage: usage}
	}
}

// String returns the status's name.
func (s Status) String() string {
	return name(int(s), "Status", "Completed", "Stopped", "Failed")
}

// String returns the reason's name.
func (r StopReason) String() string {
	return name(int(r), "StopReason", "Cancelled", "Refusal", "OutputLimit", "ContextFull")
}

// String returns the reason's name.
func (r FailureReason) String() string {
	return name(int(r), "FailureReason", "ModelError", "UnexpectedStop", "PrefixMismatch")
}

// name returns the name of the value v of an enumeration whose values start at 1, or the type and number for any
// other value, the zero value included.
func name(v int, typ string, names ...string) string {
	if v < 1 || v > len(names) {
		return typ + "(" + strconv.Itoa(v) + ")"
	}
	return names[v-1]
}
