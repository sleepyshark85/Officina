package officina_test

import (
	"bytes"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/officinatest"
)

// thinking is a block with odd spacing, key order and escapes, as a provider might send it; none of it may change.
const thinking = `{ "type":"thinking",  "thinking":"caf\u00e9 \"quoted\" \/ é \u003cb\u003e", "signature":"c2ln+/=" }`

// conversationWithEscapes returns a conversation whose blocks hold <, &, non-ASCII and \u escapes.
func conversationWithEscapes(t *testing.T) *officina.Conversation {
	t.Helper()
	model := officinatest.NewModel("scripted", officinatest.Reply{Events: []officina.ModelEvent{
		officina.BlockReceived{Block: officina.Block{Raw: jsontext.Value(thinking)}},
		officina.BlockReceived{Block: officinatest.TextBlock("Bonjour ☃ <b>&amp;</b>")},
		officina.Finished{Reason: officina.FinishEnd},
	}})
	c := &officina.Conversation{ID: "c1"}
	run(t, newAgent(t, model, tool("search", "Searches.")), c, "Salut ✓ <i> & \\u00e9", officina.RunOptions{Context: "Date: 2026-10-05."})
	return c
}

func TestConversation_AGT06_JSONRoundTripKeepsEveryBlockByteForByte(t *testing.T) {
	t.Parallel()
	// A store may rewrite the JSON it keeps; the blocks must survive it.
	tests := []struct {
		name    string
		rewrite func(*jsontext.Value) error
	}{
		{"as written", func(*jsontext.Value) error { return nil }},
		{"indented", func(v *jsontext.Value) error { return v.Indent() }},
		{"canonicalized", func(v *jsontext.Value) error { return v.Canonicalize() }},
		{"HTML-escaped", func(v *jsontext.Value) error { return v.Format(jsontext.EscapeForHTML(true)) }},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			c := conversationWithEscapes(t)
			first, err := json.Marshal(c)
			if err != nil {
				t.Fatalf("Marshal() error = %v", err)
			}
			stored := jsontext.Value(bytes.Clone(first))
			if err := tt.rewrite(&stored); err != nil {
				t.Fatalf("rewrite error = %v", err)
			}

			var restored officina.Conversation
			if err := json.Unmarshal(stored, &restored); err != nil {
				t.Fatalf("Unmarshal() error = %v", err)
			}
			second, err := json.Marshal(&restored)
			if err != nil {
				t.Fatalf("Marshal() error = %v", err)
			}

			if !bytes.Equal(first, second) {
				t.Errorf("JSON after a round trip = %s, want %s", second, first)
			}
			if diff := cmp.Diff(c.Messages(), restored.Messages()); diff != "" {
				t.Errorf("messages mismatch (-want +got):\n%s", diff)
			}
			if got := string(restored.Messages()[2].Blocks[0].Raw); got != thinking {
				t.Errorf("raw block = %s, want %s", got, thinking)
			}
		})
	}
}

func TestConversation_AGT06_TheJSONFormIsPlainAndTheSameAsDotNets(t *testing.T) {
	t.Parallel()
	const form = `{"id":"c1","fingerprint":"abc","messages":[{"role":"user","blocks":[{"text":"Hi"}]},` +
		`{"role":"assistant","blocks":[{"raw":"{\"type\":\"x\"}"}]}]}`

	var c officina.Conversation
	if err := json.Unmarshal([]byte(form), &c); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	got, err := json.Marshal(&c)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}

	want := []officina.Message{
		{Role: officina.User, Blocks: []officina.Block{{Text: "Hi"}}},
		{Role: officina.Assistant, Blocks: []officina.Block{{Raw: jsontext.Value(`{"type":"x"}`)}}},
	}
	if diff := cmp.Diff(want, c.Messages()); diff != "" {
		t.Errorf("messages mismatch (-want +got):\n%s", diff)
	}
	if c.ID != "c1" || string(got) != form {
		t.Errorf("ID = %q and JSON = %s, want %q and %s", c.ID, got, "c1", form)
	}
}

func TestConversation_AGT06_UnmarshalRejectsAnInvalidConversation(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name string
		json string
		want string
	}{
		{"not JSON", `{"messages":`, ""},
		{"unknown role", `{"messages":[{"role":"system","blocks":[{"text":"x"}]}]}`, `message 1 has unknown role "system"`},
		{"no blocks", `{"messages":[{"role":"user","blocks":[]}]}`, "message 1 has no blocks"},
		{"empty block", `{"messages":[{"role":"user","blocks":[{}]}]}`, "block 1 of message 1 has neither text nor raw JSON"},
		{"invalid raw", `{"messages":[{"role":"assistant","blocks":[{"raw":"{x"}]}]}`, "block 1 of message 1 has invalid raw JSON"},
		{"duplicate name", `{"id":"a","id":"b","messages":[]}`, "duplicate"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			c := officina.Conversation{ID: "kept"}

			err := json.Unmarshal([]byte(tt.json), &c)

			if err == nil || !strings.Contains(err.Error(), tt.want) {
				t.Errorf("Unmarshal() error = %v, want one containing %q", err, tt.want)
			}
			if c.ID != "kept" {
				t.Errorf("ID = %q after a failed Unmarshal, want it unchanged", c.ID)
			}
		})
	}
}

func FuzzConversation_AGT06_UnmarshalThenMarshalIsAFixedPoint(f *testing.F) {
	f.Add([]byte(`{"id":"c1","fingerprint":"abc","messages":[{"role":"user","blocks":[{"text":"Hi"}]},` +
		`{"role":"assistant","blocks":[{"text":"x","raw":"{\"type\":\"text\",\"text\":\"\\u00e9 <\"}"}]}]}`))
	f.Add([]byte(`{"messages":[{"role":"operator","blocks":[{"raw":" [1, 2] "}]}]}`))
	f.Fuzz(func(t *testing.T, data []byte) {
		var c officina.Conversation
		if err := json.Unmarshal(data, &c); err != nil {
			return
		}
		first, err := json.Marshal(&c)
		if err != nil {
			t.Fatalf("Marshal() of a conversation that unmarshalled: %v", err)
		}
		var again officina.Conversation
		if err := json.Unmarshal(first, &again); err != nil {
			t.Fatalf("Unmarshal() of %s: %v", first, err)
		}
		second, err := json.Marshal(&again)
		if err != nil {
			t.Fatalf("Marshal() error = %v", err)
		}
		if !bytes.Equal(first, second) {
			t.Errorf("second Marshal() = %s, want %s", second, first)
		}
	})
}
