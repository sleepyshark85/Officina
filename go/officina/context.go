package officina

import (
	"errors"
	"fmt"
	"strconv"
)

// ContextManagement is how the provider shortens a long conversation on its side: it compacts the conversation into
// a summary block, and clears old tool results from what each request shows the model. The core never edits the
// conversation itself. It is part of the prefix, so it is fixed for a conversation. The zero value asks for nothing.
type ContextManagement struct {
	// CompactAt compacts the conversation once a request's input reaches this many tokens; zero for never. Claude's
	// minimum is 50,000.
	CompactAt int64
	// ClearToolResults clears old tool results; its zero value never does.
	ClearToolResults ToolResultClearing
}

// ToolResultClearing is when old tool results are cleared, and which.
type ToolResultClearing struct {
	// After clears once the conversation holds more than this many tool calls.
	After int
	// Keep is how many of the latest tool calls keep their results.
	Keep int
	// AtLeastTokens clears only when at least this many input tokens go. Each clearing rewrites the cached tail, so
	// this keeps clearings few and worth their cost; zero clears whatever there is.
	AtLeastTokens int64
}

// clears reports whether c clears old tool results.
func (c ContextManagement) clears() bool {
	return c.ClearToolResults != ToolResultClearing{}
}

// check returns why c cannot be used with a model described by info, or nil.
func (c ContextManagement) check(info ModelInfo) error {
	t := c.ClearToolResults
	switch {
	case c.CompactAt < 0:
		return fmt.Errorf("the compaction threshold %d is negative", c.CompactAt)
	case c.clears() && t.After < 1:
		return fmt.Errorf("tool results are cleared after %d tool calls; it must be at least 1", t.After)
	case t.Keep < 0:
		return fmt.Errorf("tool results clearing keeps %d tool calls; it cannot keep fewer than 0", t.Keep)
	case t.AtLeastTokens < 0:
		return fmt.Errorf("tool results clearing clears at least %d tokens; it cannot clear fewer than 0",
			t.AtLeastTokens)
	case c.CompactAt > 0 && !info.Compacts:
		return errors.New("the model's provider does not compact conversations")
	case c.clears() && !info.ClearsToolResults:
		return errors.New("the model's provider does not clear old tool results")
	}
	return nil
}

// appendFingerprint appends c to a prefix fingerprint's JSON as the .NET implementation writes it, nothing when c
// asks for nothing: ,"contextManagement":{"compactAt":…,"clearAfter":…,"clearKeep":…,"clearAtLeastTokens":…},
// each setting only when it is set.
func (c ContextManagement) appendFingerprint(b []byte) []byte {
	if c.CompactAt == 0 && !c.clears() {
		return b
	}
	b = append(b, `,"contextManagement":{`...)
	if c.CompactAt > 0 {
		b = strconv.AppendInt(append(b, `"compactAt":`...), c.CompactAt, 10)
		if c.clears() {
			b = append(b, ',')
		}
	}
	if t := c.ClearToolResults; c.clears() {
		b = strconv.AppendInt(append(b, `"clearAfter":`...), int64(t.After), 10)
		b = strconv.AppendInt(append(b, `,"clearKeep":`...), int64(t.Keep), 10)
		b = strconv.AppendInt(append(b, `,"clearAtLeastTokens":`...), t.AtLeastTokens, 10)
	}
	return append(b, '}')
}

// CompactionReported says the provider compacted the conversation before replying; the summary block is among the
// reply's blocks.
type CompactionReported struct {
	// Tokens are the input tokens summarized.
	Tokens int64
	// SummaryTokens is the summary's size.
	SummaryTokens int64
}

// ClearingReported says the provider cleared old tool results from the request's view of the conversation.
type ClearingReported struct {
	// Tokens are the input tokens cleared.
	Tokens int64
	// ToolCalls counts the tool calls whose results were cleared.
	ToolCalls int
}

func (CompactionReported) modelEvent() {}
func (ClearingReported) modelEvent()   {}

// ConversationCompacted says the provider compacted the conversation during a model call, as CompactionReported.
type ConversationCompacted struct {
	Tokens, SummaryTokens int64
}

// ToolResultsCleared says the provider cleared old tool results for a model call, as ClearingReported. The
// conversation keeps them, so the provider clears and reports them again on every later call.
type ToolResultsCleared struct {
	Tokens    int64
	ToolCalls int
}

func (ConversationCompacted) runEvent() {}
func (ToolResultsCleared) runEvent()    {}

// compactionEntry returns the audit entry of a compaction.
func compactionEntry(e CompactionReported) AuditEntry {
	return AuditEntry{Kind: AuditCompacted, Detail: thousands(e.Tokens) + " tokens summarized into " +
		thousands(e.SummaryTokens) + "."}
}

// clearingEntry returns the audit entry of a clearing.
func clearingEntry(e ClearingReported) AuditEntry {
	return AuditEntry{Kind: AuditCleared, Detail: "Results of " + thousands(int64(e.ToolCalls)) +
		" tool calls cleared: " + thousands(e.Tokens) + " tokens."}
}
