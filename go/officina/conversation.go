package officina

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"slices"
	"strings"
	"sync/atomic"
)

// Role says who a message is from.
type Role string

// The roles of a conversation's messages.
const (
	User      Role = "user"
	Assistant Role = "assistant"
	// Operator is the host, with operator authority, after the cached prefix: the run context.
	Operator Role = "operator"
)

// Block is one piece of a message. A block the model produced keeps the provider's JSON in Raw, stored and replayed
// byte for byte; the core reads only its neutral view, Text. A block the core made, such as the user's message, has
// no raw form: the provider adapter renders it.
type Block struct {
	// Text is the text, for a text block; empty for any other.
	Text string
	// Raw is the provider's JSON for the block, exactly as received; nil for a block the core made.
	Raw jsontext.Value
}

// Message is a role and at least one block. Messages in a conversation are never changed.
type Message struct {
	Role   Role
	Blocks []Block
}

// Text returns the text of the message's blocks, joined.
func (m Message) Text() string {
	var b strings.Builder
	for _, block := range m.Blocks {
		b.WriteString(block.Text)
	}
	return b.String()
}

// textMessage returns a message of one text block.
func textMessage(role Role, text string) Message {
	return Message{Role: role, Blocks: []Block{{Text: text}}}
}

// ErrConversationInUse is returned for a run on a conversation that another run is using.
var ErrConversationInUse = errors.New("another run is using the conversation; one run at a time may use it")

// Conversation is the append-only list of messages between an agent and its model. The host owns it and stores it
// as JSON between runs (its MarshalJSON and UnmarshalJSON); the core only appends to it. The zero value is an empty
// conversation. One run at a time may use it, and nothing else may use it while a run does.
type Conversation struct {
	// ID identifies the conversation for the host, such as a session id; the core only keeps it.
	ID string

	// fingerprint is the prefix fingerprint of the agent whose reply was first appended; empty before that. A run of
	// an agent with another fingerprint fails without calling the model.
	fingerprint string
	messages    []Message
	running     atomic.Bool
}

// Messages returns the messages, oldest first. The slice is a copy, but its messages share their blocks with the
// conversation and must not be modified.
func (c *Conversation) Messages() []Message {
	return slices.Clone(c.messages)
}

// The JSON form, the same as the .NET implementation's: a block's raw JSON is kept as a JSON string, so it reads back
// byte for byte however the host's store or encoder rewrites the conversation's JSON.
type (
	conversationJSON struct {
		ID          string        `json:"id"`
		Fingerprint string        `json:"fingerprint,omitzero"`
		Messages    []messageJSON `json:"messages"`
	}
	messageJSON struct {
		Role   Role        `json:"role"`
		Blocks []blockJSON `json:"blocks"`
	}
	blockJSON struct {
		Text string `json:"text,omitzero"`
		Raw  string `json:"raw,omitzero"`
	}
)

// MarshalJSON writes the conversation in its JSON form.
func (c *Conversation) MarshalJSON() ([]byte, error) {
	wire := conversationJSON{ID: c.ID, Fingerprint: c.fingerprint, Messages: make([]messageJSON, len(c.messages))}
	for i, m := range c.messages {
		wire.Messages[i] = messageJSON{Role: m.Role, Blocks: make([]blockJSON, len(m.Blocks))}
		for j, b := range m.Blocks {
			wire.Messages[i].Blocks[j] = blockJSON{Text: b.Text, Raw: string(b.Raw)}
		}
	}
	data, err := json.Marshal(wire)
	if err != nil {
		return nil, fmt.Errorf("marshal conversation: %w", err)
	}
	return data, nil
}

// UnmarshalJSON reads a conversation from its JSON form. It checks each message has a known role and at least one
// block, and each block has text or raw JSON, which must be valid.
func (c *Conversation) UnmarshalJSON(data []byte) error {
	var wire conversationJSON
	if err := json.Unmarshal(data, &wire); err != nil {
		return fmt.Errorf("unmarshal conversation: %w", err)
	}
	messages := make([]Message, len(wire.Messages))
	for i, m := range wire.Messages {
		if m.Role != User && m.Role != Assistant && m.Role != Operator {
			return fmt.Errorf("unmarshal conversation: message %d has unknown role %q", i+1, m.Role)
		}
		if len(m.Blocks) == 0 {
			return fmt.Errorf("unmarshal conversation: message %d has no blocks", i+1)
		}
		messages[i] = Message{Role: m.Role, Blocks: make([]Block, len(m.Blocks))}
		for j, b := range m.Blocks {
			block := Block{Text: b.Text}
			if b.Raw != "" {
				block.Raw = jsontext.Value(b.Raw)
				if !block.Raw.IsValid() {
					return fmt.Errorf("unmarshal conversation: block %d of message %d has invalid raw JSON", j+1, i+1)
				}
			}
			if block.Text == "" && block.Raw == nil {
				return fmt.Errorf("unmarshal conversation: block %d of message %d has neither text nor raw JSON", j+1, i+1)
			}
			messages[i].Blocks[j] = block
		}
	}
	c.ID, c.fingerprint, c.messages = wire.ID, wire.Fingerprint, messages
	return nil
}
