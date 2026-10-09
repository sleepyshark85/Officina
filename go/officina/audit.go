package officina

import (
	"context"
	"crypto/rand"
	"encoding/json/v2"
	"fmt"
	"os"
	"strconv"
	"sync"
	"time"
)

// AuditSink is where an agent's audit trail goes. A run's entries arrive one at a time, in sequence order. A write
// that returns nil is durable; a sink never drops an entry silently, but returns the error.
type AuditSink interface {
	Write(ctx context.Context, e AuditEntry) error
}

// AuditEntry is one durable record of a run: when, in which order, of which run, conversation and agent, and what.
// Its text is redacted of the agent's secrets and truncated with its size noted.
type AuditEntry struct {
	Time time.Time `json:"time"`
	// Sequence is the entry's place in its run, from 1. A gap is an entry the sink failed to write.
	Sequence     int64     `json:"sequence"`
	Run          string    `json:"run"`
	Conversation string    `json:"conversation,omitzero"`
	Agent        string    `json:"agent,omitzero"`
	Kind         AuditKind `json:"kind"`
	Tool         string    `json:"tool,omitzero"`
	CallID       string    `json:"callId,omitzero"`
	// Input is a tool call's input, as JSON text.
	Input string `json:"input,omitzero"`
	// Outcome is a short word for how it went: a run's status and reason, a call's ok or error, an approval's
	// approved or denied.
	Outcome string `json:"outcome,omitzero"`
	// Detail says more: a tool's result or error, a denial's reason, why a run stopped or failed.
	Detail string `json:"detail,omitzero"`
	// Duration is how long a tool ran; the JSON-lines sink writes it in nanoseconds.
	Duration time.Duration `json:"duration,omitzero"`
	// Usage is a run's tokens, on its end entry.
	Usage Usage `json:"usage,omitzero"`
}

// AuditKind is what an audit entry records.
type AuditKind string

// The kinds of audit entry.
const (
	AuditRunStarted AuditKind = "RunStarted"
	// AuditRunEnded holds the run's result, refusals, model failures and prefix mismatches included, with its usage.
	AuditRunEnded AuditKind = "RunEnded"
	// AuditToolStarted says a tool call is about to run; a write runs only once it is recorded.
	AuditToolStarted AuditKind = "ToolStarted"
	// AuditToolEnded is a tool call's outcome: every call has one, whether it ran or not.
	AuditToolEnded        AuditKind = "ToolEnded"
	AuditApprovalAsked    AuditKind = "ApprovalAsked"
	AuditApprovalAnswered AuditKind = "ApprovalAnswered"
)

// maxAuditText is the longest text an entry keeps per field, in bytes.
const maxAuditText = 4000

// recorder numbers a run's audit entries and writes them one at a time. Without a sink it records nothing, and
// every record succeeds.
type recorder struct {
	agent        *Agent
	run          string
	conversation string

	mu       sync.Mutex
	sequence int64
}

func newRecorder(a *Agent, conversation string) *recorder {
	return &recorder{agent: a, run: rand.Text(), conversation: conversation}
}

// record fills in e's time, sequence and identity, redacts and truncates its text, and writes it. The trail is
// written even for a cancelled run, so ctx's cancellation does not reach the sink. A failure is returned only for
// the caller to decide what a missing entry means: only a write tool's attempt depends on it.
func (r *recorder) record(ctx context.Context, e AuditEntry) error {
	sink := r.agent.auditSink
	if sink == nil {
		return nil
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.sequence++
	e.Time, e.Sequence, e.Run, e.Conversation, e.Agent = time.Now(), r.sequence, r.run, r.conversation, r.agent.name
	e.Input, e.Detail = r.clean(e.Input), r.clean(e.Detail)
	if err := sink.Write(context.WithoutCancel(ctx), e); err != nil {
		return fmt.Errorf("audit %s: %w", e.Kind, err)
	}
	return nil
}

func (r *recorder) clean(text string) string {
	text = r.agent.redact(text)
	if len(text) <= maxAuditText {
		return text
	}
	return cut(text, maxAuditText) + "… [truncated: " + strconv.Itoa(len(text)) + " bytes]"
}

// JSONLinesSink appends audit entries to a file, one JSON object per line, each synced to disk before Write
// returns. It is safe for concurrent runs within one process. Create it with NewJSONLinesSink.
type JSONLinesSink struct {
	path string
	mu   sync.Mutex
}

// NewJSONLinesSink returns a sink that appends to the file at path, creating it if needed.
func NewJSONLinesSink(path string) *JSONLinesSink {
	return &JSONLinesSink{path: path}
}

// Write appends e to the file as one line.
func (s *JSONLinesSink) Write(_ context.Context, e AuditEntry) (err error) {
	line, err := json.Marshal(e, json.WithMarshalers(json.MarshalFunc(func(d time.Duration) ([]byte, error) {
		return strconv.AppendInt(nil, int64(d), 10), nil
	})))
	if err != nil {
		return fmt.Errorf("write audit entry: %w", err)
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	f, err := os.OpenFile(s.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o600)
	if err != nil {
		return fmt.Errorf("write audit entry: %w", err)
	}
	defer func() {
		if closeErr := f.Close(); err == nil && closeErr != nil {
			err = fmt.Errorf("write audit entry: %w", closeErr)
		}
	}()
	if _, err := f.Write(append(line, '\n')); err != nil {
		return fmt.Errorf("write audit entry: %w", err)
	}
	if err := f.Sync(); err != nil {
		return fmt.Errorf("write audit entry: %w", err)
	}
	return nil
}
