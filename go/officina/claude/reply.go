package claude

import (
	"context"
	"encoding/json/jsontext"
	"errors"
	"fmt"

	"github.com/anthropics/anthropic-sdk-go"

	"github.com/sleepyshark85/officina/go/officina"
)

// errIncomplete is a stream that ended cleanly before the reply did, as a dropped connection can.
var errIncomplete = errors.New("the reply's stream ended before the reply")

// attempt makes one streamed call and yields what it streams: text deltas, each block once complete, the call's
// usage, then the finish. It returns the call's failure, if any, after yielding the usage reported until then, or
// stopped once yield returns false.
func (m *Model) attempt(
	ctx context.Context, params anthropic.BetaMessageNewParams, yield func(officina.ModelEvent, error) bool,
) (stopped bool, err error) {
	stream := m.client.Beta.Messages.NewStreaming(ctx, params)
	// Closing the body is the only clean-up the stream needs; its error says nothing about the reply.
	defer func() { _ = stream.Close() }()

	var (
		reply   anthropic.BetaMessage
		started bool
		done    bool
	)
	for !done && stream.Next() {
		event := stream.Current()
		if err := reply.Accumulate(event); err != nil {
			return false, fmt.Errorf("read the reply: %w", err)
		}
		var next officina.ModelEvent
		switch event.Type {
		case "message_start":
			started = true
		case "content_block_delta":
			if event.Delta.Type == "text_delta" {
				next = officina.TextDelta{Text: event.Delta.Text}
			}
		case "content_block_stop":
			block, err := blockOf(reply.Content[event.Index])
			if err != nil {
				return false, err
			}
			next = officina.BlockReceived{Block: block}
		case "message_stop":
			done = true
		}
		if next != nil && !yield(next, nil) {
			return true, nil
		}
	}
	err = stream.Err()
	if err == nil && !done {
		err = errIncomplete
	}
	if err != nil && (!started || ctx.Err() != nil) {
		return false, err
	}
	// The tokens of an attempt that failed mid-stream are billed, so they are reported too.
	if !yield(officina.UsageReceived{Usage: usage(reply.Usage)}, nil) {
		return true, nil
	}
	if err != nil {
		return false, err
	}
	return !yield(finish(reply), nil), nil
}

// blockOf returns a reply block as the conversation keeps it: its JSON in the canonical form, with its text for a
// text block and its call for a tool use.
func blockOf(b anthropic.BetaContentBlockUnion) (officina.Block, error) {
	raw, err := canonical(b.RawJSON())
	if err != nil {
		return officina.Block{}, fmt.Errorf("read a %s block: %w", b.Type, err)
	}
	block := officina.Block{Raw: raw}
	switch b.Type {
	case "text":
		block.Text = b.Text
	case "tool_use":
		// The union's raw input, as the model wrote it: the typed block's input is already decoded.
		block.ToolCall = &officina.ToolCall{ID: b.ID, Name: b.Name, Input: jsontext.Value(b.Input).Clone()}
	}
	return block, nil
}

// usage returns the call's tokens. When the call ran several iterations (a compaction, then the reply), the totals
// count only the last, so the iterations are added up instead, whatever their kind.
func usage(u anthropic.BetaUsage) officina.Usage {
	if len(u.Iterations) == 0 {
		return officina.Usage{
			Input: u.InputTokens, Output: u.OutputTokens,
			CacheRead: u.CacheReadInputTokens, CacheWrite: u.CacheCreationInputTokens,
		}
	}
	var sum officina.Usage
	for _, it := range u.Iterations {
		sum.Input += it.InputTokens
		sum.Output += it.OutputTokens
		sum.CacheRead += it.CacheReadInputTokens
		sum.CacheWrite += it.CacheCreationInputTokens
	}
	return sum
}

// finish maps the stop reason by its word; a word this package does not know keeps it as the detail.
func finish(reply anthropic.BetaMessage) officina.Finished {
	switch reason := string(reply.StopReason); reason {
	case "end_turn":
		return officina.Finished{Reason: officina.FinishEnd}
	case "tool_use":
		return officina.Finished{Reason: officina.FinishToolUse}
	case "max_tokens":
		return officina.Finished{Reason: officina.FinishMaxTokens}
	case "model_context_window_exceeded":
		return officina.Finished{Reason: officina.FinishContextFull}
	case "refusal":
		return officina.Finished{Reason: officina.FinishRefusal, Detail: string(reply.StopDetails.Category)}
	default:
		return officina.Finished{Reason: officina.FinishUnknown, Detail: reason}
	}
}
