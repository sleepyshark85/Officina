package officinatest

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
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

// ToolUseReply returns a reply that asks for the tool calls of blocks, made by ToolUseBlock, and stops for them.
func ToolUseReply(blocks ...officina.Block) Reply {
	var events []officina.ModelEvent
	for _, b := range blocks {
		events = append(events, officina.BlockReceived{Block: b})
	}
	return Reply{Events: append(events, officina.Finished{Reason: officina.FinishToolUse})}
}

// ToolUseBlock returns a block that calls tool name with input, a JSON object, as a provider adapter stores it. An
// input that is not valid JSON is kept in the call as it is, as a model may write one.
func ToolUseBlock(id, name, input string) officina.Block {
	type use struct {
		Type  string         `json:"type"`
		ID    string         `json:"id"`
		Name  string         `json:"name"`
		Input jsontext.Value `json:"input"`
	}
	raw, err := json.Marshal(use{"tool_use", id, name, jsontext.Value(input)}, jsontext.EscapeForHTML(true))
	if err != nil {
		// The provider sends what it parsed, so its raw form of an invalid input holds an empty object.
		raw, err = json.Marshal(use{"tool_use", id, name, jsontext.Value(`{}`)}, jsontext.EscapeForHTML(true))
	}
	if err != nil {
		panic(fmt.Sprintf("officinatest: tool use block of invalid UTF-8: %v", err)) //nolint:forbidigo // A test kit misused.
	}
	return officina.Block{Raw: raw, ToolCall: &officina.ToolCall{ID: id, Name: name, Input: jsontext.Value(input)}}
}

// Model is a model that answers with replies scripted in advance, in order, and records every request. It needs no
// network or API key, and many runs may use it at once. Like a provider's API, it rejects a request that
// CheckConversation rejects, and then keeps its reply for the next request.
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

// Info returns the provider and name "scripted", and no price. To script another, embed the Model in a type of
// your own with an Info method.
func (m *Model) Info() officina.ModelInfo {
	return officina.ModelInfo{Provider: "scripted", Name: "scripted"}
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
	if err := CheckConversation(req.Messages); err != nil {
		return Reply{}, fmt.Errorf("scripted model: request %d is invalid: %w", len(m.requests), err)
	}
	if len(m.replies) == 0 {
		return Reply{}, fmt.Errorf("scripted model: request %d has no reply left", len(m.requests))
	}
	reply := m.replies[0]
	m.replies = m.replies[1:]
	return reply, nil
}
