package bookshop

import (
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
)

func TestTranscript_APP15_NamesEachResultsCallMarksErrorsAndCutsLongResults(t *testing.T) {
	t.Parallel()
	long := strings.Repeat("é", 1_001)
	stored := `{"id":"s1","messages":[` +
		`{"role":"user","blocks":[{"text":"Order Emma."}]},` +
		`{"role":"operator","blocks":[{"text":"Today is Friday."}]},` +
		`{"role":"assistant","blocks":[{"text":"Looking."},` +
		`{"raw":"{}","toolCall":{"id":"c1","name":"search_books","input":"{\"title\":\"Emma\"}"}},` +
		`{"raw":"{}","toolCall":{"id":"c2","name":"place_order","input":"{}"}}]},` +
		`{"role":"user","blocks":[{"toolResult":{"callId":"c1","content":"` + long + `","isError":false}},` +
		`{"toolResult":{"callId":"c2","content":"Not enough stock.","isError":true}},` +
		`{"toolResult":{"callId":"c9","content":"Gone.","isError":true}}]},` +
		`{"role":"assistant","blocks":[{"text":"Emma is out of stock."}]}]}`
	var c officina.Conversation
	if err := json.Unmarshal([]byte(stored), &c); err != nil {
		t.Fatalf("unmarshal the conversation: %v", err)
	}

	got := transcript(&c)

	want := "Staff: Order Emma.\n" +
		"Assistant: Looking.\n" +
		`Tool call search_books {"title":"Emma"}` + "\n" +
		"Tool call place_order {}\n" +
		"Tool result of search_books: " + strings.Repeat("é", 1_000) + " […]\n" +
		"Tool result of place_order (error): Not enough stock.\n" +
		"Tool result of a call (error): Gone.\n" +
		"Assistant: Emma is out of stock.\n"
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("transcript mismatch (-want +got):\n%s", diff)
	}
}
