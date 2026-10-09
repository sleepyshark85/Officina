package officina

import (
	"context"
	"iter"
)

// Model is a provider's model with fixed settings: the only way the core reaches a model. Many runs may stream from
// one Model at once.
type Model interface {
	// Settings returns the model and every setting that shapes its requests, as text that changes when any of them
	// does. It is part of the prefix fingerprint, so it must not change during the Model's life.
	Settings() string
	// Stream sends one request and yields the reply: text deltas and complete blocks as they arrive, usage, and last
	// a Finished. The Model retries transient failures itself; a failure that remains is yielded as an error, which
	// ends the reply. When ctx is done or the caller stops early, Stream releases everything it started.
	//
	// A caller that stops early does so by yield returning false; ctx is cancelled only after the stream has
	// returned. So a stream that waits for a goroutine of its own must first stop it on that signal too, not only
	// on ctx, or it waits forever.
	Stream(ctx context.Context, req Request) iter.Seq2[ModelEvent, error]
}

// Request is one model call: the prefix that stays the same for a conversation (tools and instructions; the model's
// settings are its own), then the conversation with the run's pending messages. A Model must not modify it.
type Request struct {
	// Tools are sorted by name.
	Tools        []Tool
	Instructions string
	Messages     []Message
}

// Usage counts tokens as the provider bills them.
type Usage struct {
	// Input counts the input tokens neither read from nor written to the cache.
	Input      int64
	Output     int64
	CacheRead  int64
	CacheWrite int64
}

func (u Usage) plus(v Usage) Usage {
	return Usage{u.Input + v.Input, u.Output + v.Output, u.CacheRead + v.CacheRead, u.CacheWrite + v.CacheWrite}
}

// ModelEvent is something a model streams while it replies: a TextDelta, BlockReceived, UsageReceived or Finished.
type ModelEvent interface {
	modelEvent()
}

// TextDelta is a piece of reply text, for display as it streams; the complete block follows in a BlockReceived.
type TextDelta struct {
	Text string
}

// BlockReceived is a complete block of the reply, in reply order.
type BlockReceived struct {
	Block Block
}

// UsageReceived reports the tokens used since the call's previous report: reports are increments.
type UsageReceived struct {
	Usage Usage
}

// Finished says why the model stopped: the reply's last event.
type Finished struct {
	Reason FinishReason
	// Detail is a refusal's category, or the provider's own word for an unknown reason.
	Detail string
}

func (TextDelta) modelEvent()     {}
func (BlockReceived) modelEvent() {}
func (UsageReceived) modelEvent() {}
func (Finished) modelEvent()      {}

// FinishReason is why a model stopped, in the model contract's words.
type FinishReason int

// The reasons a model stops. The zero value is FinishUnknown.
const (
	// FinishUnknown is a reason the provider gave that is none of the others.
	FinishUnknown FinishReason = iota
	FinishEnd
	FinishToolUse
	FinishMaxTokens
	FinishRefusal
	FinishContextFull
)

// String returns the reason's name.
func (r FinishReason) String() string {
	if r == FinishUnknown {
		return "FinishUnknown"
	}
	return name(int(r), "FinishReason", "FinishEnd", "FinishToolUse", "FinishMaxTokens", "FinishRefusal",
		"FinishContextFull")
}
