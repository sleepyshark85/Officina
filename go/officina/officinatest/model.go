package officinatest

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"iter"
	"slices"
	"sync"

	"github.com/sleepyshark85/officina/go/officina"
)

// Reply is one scripted model call: the events it streams, in order, then Err if it is set.
type Reply struct {
	Events []officina.ModelEvent
	Err    error
}

// TextReply returns a reply that streams text, then its block, and ends.
func TextReply(text string) Reply {
	return Reply{Events: []officina.ModelEvent{
		officina.TextDelta{Text: text},
		officina.BlockReceived{Block: TextBlock(text)},
		officina.Finished{Reason: officina.FinishEnd},
	}}
}

// TextBlock returns a text block as a provider adapter stores it: its raw JSON is compact, with <, > and & escaped.
func TextBlock(text string) officina.Block {
	raw, err := json.Marshal(struct {
		Type string `json:"type"`
		Text string `json:"text"`
	}{"text", text}, jsontext.EscapeForHTML(true))
	if err != nil {
		// Only invalid UTF-8 fails to marshal, and the text block of such text cannot exist.
		panic(fmt.Sprintf("officinatest: text block of invalid UTF-8: %v", err)) //nolint:forbidigo // A test kit misused.
	}
	return officina.Block{Text: text, Raw: raw}
}

// Model is a model that answers with replies scripted in advance, in order, and records every request. It needs no
// network or API key, and many runs may use it at once. Like a provider's API, it rejects a request whose roles are
// out of order, and then keeps its reply for the next request.
type Model struct {
	settings string

	mu       sync.Mutex
	replies  []Reply
	requests []officina.Request
}

// NewModel returns a model with the given settings, which enter the prefix fingerprint, and replies.
func NewModel(settings string, replies ...Reply) *Model {
	return &Model{settings: settings, replies: replies}
}

// Settings returns the settings the model was made with.
func (m *Model) Settings() string {
	return m.settings
}

// Requests returns the requests received so far, in order.
func (m *Model) Requests() []officina.Request {
	m.mu.Lock()
	defer m.mu.Unlock()
	return slices.Clone(m.requests)
}

// Stream records req and streams the next reply. It yields an error when req is invalid or no reply is left, and
// ctx's error when ctx is done before an event.
func (m *Model) Stream(ctx context.Context, req officina.Request) iter.Seq2[officina.ModelEvent, error] {
	return func(yield func(officina.ModelEvent, error) bool) {
		reply, err := m.next(req)
		if err != nil {
			yield(nil, err)
			return
		}
		for _, event := range reply.Events {
			if ctx.Err() != nil {
				yield(nil, ctx.Err())
				return
			}
			if !yield(event, nil) {
				return
			}
		}
		if reply.Err != nil {
			yield(nil, reply.Err)
		}
	}
}

func (m *Model) next(req officina.Request) (Reply, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.requests = append(m.requests, req)
	if err := checkRoles(req.Messages); err != nil {
		return Reply{}, fmt.Errorf("scripted model: request %d is invalid: %w", len(m.requests), err)
	}
	if len(m.replies) == 0 {
		return Reply{}, fmt.Errorf("scripted model: request %d has no reply left", len(m.requests))
	}
	reply := m.replies[0]
	m.replies = m.replies[1:]
	return reply, nil
}

// checkRoles checks the provider's rules for the order of roles: the conversation starts with a user message, no
// two messages in a row have one role, and an operator message follows a user message and is last or followed by an
// assistant message.
func checkRoles(messages []officina.Message) error {
	if len(messages) == 0 || messages[0].Role != officina.User {
		return errors.New("the first message is not the user's")
	}
	for i := 1; i < len(messages); i++ {
		previous, role := messages[i-1].Role, messages[i].Role
		switch {
		case role == previous:
			return fmt.Errorf("messages %d and %d have one role, %s", i, i+1, role)
		case role == officina.Operator && previous != officina.User:
			return fmt.Errorf("operator message %d does not follow a user message", i+1)
		case previous == officina.Operator && role != officina.Assistant:
			return fmt.Errorf("operator message %d is followed by a %s message", i, role)
		}
	}
	return nil
}
