package claude_test

import (
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

func TestModel_MEM01_TheMemoryToolIsSentAsClaudesOwnInSortedOrderAndItsCallsRunInTheRunsScope(t *testing.T) {
	t.Parallel()
	// A reply that asks the memory tool to create a file, as Claude streams it.
	create := events(start,
		`{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_m1","name":"memory","input":{}}}`,
		`{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"create\",\"path\":\"/memories/prefs.md\","}}`,
		`{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"file_text\":\"Prices with tax.\"}"}}`,
		`{"type":"content_block_stop","index":0}`,
		`{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}`,
		`{"type":"message_stop"}`)
	api := serve(t, sse(create), sse(textReply("end_turn")))
	store := &officina.MapMemoryStore{}
	object := jsontext.Value(`{"type":"object"}`)
	a, err := officina.NewAgent(model(t, api, claude.Options{}), "Answer briefly.", officina.AgentOptions{Tools: []officina.Tool{
		{Name: "search", Description: "Searches.", InputSchema: object, Kind: officina.Read, Handler: unused},
		officina.NewMemoryTool(store),
		{Name: "add", Description: "Adds.", InputSchema: object, Kind: officina.Write, Handler: unused},
	}})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}

	res, err := a.Run(t.Context(), nil, "I prefer prices with tax.", officina.RunOptions{MemoryScope: "sam"})
	if err != nil || res.Status != officina.Completed {
		t.Fatalf("Run() = %v, %v; want it completed", res, err)
	}

	if text, err := store.Read(context.Background(), "sam", "prefs.md"); err != nil || text != "Prices with tax." {
		t.Errorf("Read() = %q, %v; want the file the call created", text, err)
	}
	for i, sent := range api.Requests() {
		var body struct {
			Tools []jsontext.Value `json:"tools"`
		}
		if err := json.Unmarshal([]byte(sent), &body); err != nil {
			t.Fatalf("unmarshal request %d: %v", i+1, err)
		}
		var names []string
		for _, tool := range body.Tools {
			var named struct {
				Name string `json:"name"`
			}
			if err := json.Unmarshal(tool, &named); err != nil {
				t.Fatalf("unmarshal a tool: %v", err)
			}
			names = append(names, named.Name)
		}
		if diff := cmp.Diff([]string{"add", "memory", "search"}, names); diff != "" {
			t.Errorf("request %d's tools mismatch (-want +got):\n%s", i+1, diff)
		}
		if got, want := string(body.Tools[1]), `{"name":"memory","type":"memory_20250818"}`; got != want {
			t.Errorf("request %d's memory tool = %s, want %s", i+1, got, want)
		}
	}
}
