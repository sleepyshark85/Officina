package claude_test

import (
	"encoding/json/jsontext"
	"encoding/json/v2"
	"strings"
	"testing"

	"github.com/google/go-cmp/cmp"

	"github.com/sleepyshark85/officina/go/officina"
	"github.com/sleepyshark85/officina/go/officina/claude"
)

func TestModel_CTX01_CTX02_CTX03_MDL02_MDL03_RequestMatchesTheSharedGoldenLayout(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(fixture(t, "claude/thinking-text.sse")), sse(textReply("end_turn")))
	m, err := claude.New(claude.Opus55, claude.EffortHigh, api.options(claude.Options{
		MaxOutputTokens: 8000, PrefixCache: claude.CacheOneHour, ConversationCache: claude.CacheFiveMinutes,
	}))
	if err != nil {
		t.Fatalf("New() error = %v", err)
	}
	// The tools are given out of order: the request has them sorted.
	a, err := officina.NewAgent(m, "You are the assistant of a bookshop.", officina.AgentOptions{Tools: []officina.Tool{
		{Name: "search", Description: "Searches the catalogue.", InputSchema: jsontext.Value(
			`{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}`),
			Kind: officina.Read, Handler: unused},
		{Name: "add_to_cart", Description: "Adds a book to the cart.", InputSchema: jsontext.Value(
			`{"type":"object","properties":{"isbn":{"type":"string"},"copies":{"type":"integer"}},"required":["isbn"]}`),
			Kind: officina.Write, Handler: unused},
	}})
	if err != nil {
		t.Fatalf("NewAgent() error = %v", err)
	}
	var c officina.Conversation
	opts := officina.RunOptions{Context: "Today is 2026-10-05."}

	for _, message := range []string{"Is Gaudy Night in stock?", "And its price?"} {
		if _, err := a.Run(t.Context(), &c, message, opts); err != nil {
			t.Fatalf("Run(%q) error = %v", message, err)
		}
	}

	requests := api.Requests()
	if len(requests) != 2 {
		t.Fatalf("got %d requests, want 2", len(requests))
	}
	var body map[string]any
	if err := json.Unmarshal([]byte(requests[1]), &body); err != nil {
		t.Fatalf("unmarshal the request: %v", err)
	}
	if body["stream"] != true {
		t.Errorf("stream = %v, want true", body["stream"])
	}
	delete(body, "stream")
	var golden map[string]any
	if err := json.Unmarshal([]byte(fixture(t, "claude/request-layout.json")), &golden); err != nil {
		t.Fatalf("unmarshal the golden layout: %v", err)
	}
	if diff := cmp.Diff(golden, body); diff != "" {
		t.Errorf("request layout mismatch (-want +got):\n%s", diff)
	}
	// Parsed JSON hides how blocks are written: the stored ones must be on the wire byte for byte.
	for _, b := range c.Messages()[2].Blocks {
		if !strings.Contains(requests[1], string(b.Raw)) {
			t.Errorf("stored block %s is not in the request byte for byte", b.Raw)
		}
	}
}

func TestModel_CTX01_ARequestWithoutToolsIsLaidOutAsDotNetsIs(t *testing.T) {
	t.Parallel()
	// What the .NET implementation sends for the same request, captured from its fake API: an empty tool list
	// included.
	const dotnet = `{"model":"claude-opus-5-5","max_tokens":64000,"thinking":{"type":"adaptive"},` +
		`"output_config":{"effort":"medium"},"cache_control":{"type":"ephemeral","ttl":"5m"},"tools":[],` +
		`"system":[{"type":"text","text":"Answer briefly.","cache_control":{"type":"ephemeral","ttl":"5m"}}],` +
		`"messages":[{"role":"user","content":[{"type":"text","text":"Hi"}]}],"stream":true}`
	api := serve(t, sse(textReply("end_turn")))

	if _, err := collect(t.Context(), model(t, api, claude.Options{}), hi()); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	var want, got any
	if err := json.Unmarshal([]byte(dotnet), &want); err != nil {
		t.Fatalf("unmarshal .NET's request: %v", err)
	}
	if err := json.Unmarshal([]byte(api.Requests()[0]), &got); err != nil {
		t.Fatalf("unmarshal the request: %v", err)
	}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("request mismatch (-want +got):\n%s", diff)
	}
}

func TestNew_MDL02_TheAPIKeyGivenIsSent(t *testing.T) {
	t.Parallel()
	api := serve(t, sse(textReply("end_turn")))

	if _, err := collect(t.Context(), model(t, api, claude.Options{}), hi()); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	api.mu.Lock()
	defer api.mu.Unlock()
	if diff := cmp.Diff([]string{"test-key"}, api.keys); diff != "" {
		t.Errorf("API keys mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_BUD01_AnOutputLimitTheBudgetLowersIsSentAndAHigherOneIsNot(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name        string
		limit, sent int64
	}{
		{"lower than the model's", 360, 360},
		{"higher than the model's", 5000, 2000},
		{"none", 0, 2000},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			api := serve(t, sse(textReply("end_turn")))
			req := hi()
			req.MaxOutputTokens = tt.limit

			if _, err := collect(t.Context(), model(t, api, claude.Options{MaxOutputTokens: 2000}), req); err != nil {
				t.Fatalf("Stream() error = %v", err)
			}

			var body struct {
				MaxTokens int64 `json:"max_tokens"`
			}
			if err := json.Unmarshal([]byte(api.Requests()[0]), &body); err != nil {
				t.Fatalf("unmarshal the request: %v", err)
			}
			if body.MaxTokens != tt.sent {
				t.Errorf("max_tokens = %d, want %d", body.MaxTokens, tt.sent)
			}
		})
	}
}

func TestModel_CTX06_ToolResultsGoOutAsOneUserMessageOfToolResultBlocks(t *testing.T) {
	t.Parallel()
	use := `{"type":"tool_use","id":"toolu_01","name":"search","input":{"q":"Emma"}}`
	req := hi()
	req.Messages = append(req.Messages,
		officina.Message{Role: officina.Assistant, Blocks: []officina.Block{{Raw: jsontext.Value(use), ToolCall: &officina.ToolCall{
			ID: "toolu_01", Name: "search", Input: jsontext.Value(`{"q":"Emma"}`),
		}}}},
		officina.Message{Role: officina.User, Blocks: []officina.Block{
			{ToolResult: &officina.ToolResult{CallID: "toolu_01", Content: "3 copies <new>"}},
			{ToolResult: &officina.ToolResult{CallID: "toolu_02", Content: "failed", IsError: true}},
		}})
	api := serve(t, sse(textReply("end_turn")))

	if _, err := collect(t.Context(), model(t, api, claude.Options{}), req); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	var sent struct {
		Messages []jsontext.Value `json:"messages"`
	}
	if err := json.Unmarshal([]byte(api.Requests()[0]), &sent); err != nil {
		t.Fatalf("unmarshal the request: %v", err)
	}
	var got any
	if err := json.Unmarshal(sent.Messages[2], &got); err != nil {
		t.Fatalf("unmarshal the message: %v", err)
	}
	want := map[string]any{"role": "user", "content": []any{
		map[string]any{"type": "tool_result", "tool_use_id": "toolu_01", "content": "3 copies <new>"},
		map[string]any{"type": "tool_result", "tool_use_id": "toolu_02", "content": "failed", "is_error": true},
	}}
	if diff := cmp.Diff(want, got); diff != "" {
		t.Errorf("results message mismatch (-want +got):\n%s", diff)
	}
}

func TestModel_MDL05_BlocksTheDotNetImplementationStoredReachTheWireByteForByte(t *testing.T) {
	t.Parallel()
	var c officina.Conversation
	if err := json.Unmarshal([]byte(fixture(t, "conversation/conversation.json")), &c); err != nil {
		t.Fatalf("unmarshal the conversation: %v", err)
	}
	var blocks struct {
		Thinking string `json:"thinking"`
		Text     string `json:"text"`
	}
	if err := json.Unmarshal([]byte(fixture(t, "claude/blocks.json")), &blocks); err != nil {
		t.Fatalf("unmarshal the blocks: %v", err)
	}
	stored := c.Messages()[2].Blocks
	if string(stored[0].Raw) != blocks.Thinking || string(stored[1].Raw) != blocks.Text {
		t.Fatalf("the shared conversation's blocks %s and %s differ from blocks.json's", stored[0].Raw, stored[1].Raw)
	}
	api := serve(t, sse(textReply("end_turn")))
	req := officina.Request{Instructions: "Answer briefly.", Messages: append(c.Messages(),
		officina.Message{Role: officina.User, Blocks: []officina.Block{{Text: "And its price?"}}})}

	if _, err := collect(t.Context(), model(t, api, claude.Options{}), req); err != nil {
		t.Fatalf("Stream() error = %v", err)
	}

	want := `{"role":"assistant","content":[` + blocks.Thinking + `,` + blocks.Text + `]}`
	if sent := api.Requests()[0]; !strings.Contains(sent, want) {
		t.Errorf("request %s does not hold the assistant message %s byte for byte", sent, want)
	}
}

func TestModel_MDL05_AGT06_AReplyIsReplayedUnchangedAfterSaveAndResume(t *testing.T) {
	t.Parallel()
	reply := events(append(append([]string{start},
		textEvents("<b>Fish & chips</b>", " cost 5 € ")...),
		`{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}`,
		`{"type":"message_stop"}`)...)
	api := serve(t, sse(reply), sse(textReply("end_turn")))
	a := agent(t, model(t, api, claude.Options{}))
	var c officina.Conversation
	if _, err := a.Run(t.Context(), &c, "What's for dinner?", officina.RunOptions{}); err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	saved, err := json.Marshal(&c)
	if err != nil {
		t.Fatalf("Marshal() error = %v", err)
	}
	var resumed officina.Conversation
	if err := json.Unmarshal(saved, &resumed); err != nil {
		t.Fatalf("Unmarshal() error = %v", err)
	}
	if _, err := a.Run(t.Context(), &resumed, "Thanks.", officina.RunOptions{}); err != nil {
		t.Fatalf("Run() error = %v", err)
	}

	raw := string(c.Messages()[1].Blocks[0].Raw)
	if want := esc(`{"type":"text","text":"%u003cb%u003eFish %u0026 chips%u003c/b%u003e cost 5 € "}`); raw != want {
		t.Errorf("stored block = %s, want %s", raw, want)
	}
	if want := `{"role":"assistant","content":[` + raw + `]}`; !strings.Contains(api.Requests()[1], want) {
		t.Errorf("request %s does not hold the assistant message %s byte for byte", api.Requests()[1], want)
	}
}

func TestNew_MDL03_SettingsNameEverySettingThatShapesARequest(t *testing.T) {
	t.Parallel()
	tests := []struct {
		effort claude.Effort
		opts   claude.Options
		want   string
	}{
		{claude.EffortMedium, claude.Options{},
			"claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=5m thinking=adaptive"},
		{claude.EffortHigh, claude.Options{MaxOutputTokens: 2000, PrefixCache: claude.CacheOneHour,
			ConversationCache: claude.CacheOneHour},
			"claude model=claude-opus-5-5 effort=high max_tokens=2000 cache=1h thinking=adaptive"},
		{claude.EffortMax, claude.Options{PrefixCache: claude.CacheOneHour},
			"claude model=claude-opus-5-5 effort=max max_tokens=64000 cache=1h/5m thinking=adaptive"},
	}
	for _, tt := range tests {
		t.Run(tt.want, func(t *testing.T) {
			t.Parallel()
			m, err := claude.New(claude.Opus55, tt.effort, tt.opts)
			if err != nil {
				t.Fatalf("New() error = %v", err)
			}

			if got := m.Settings(); got != tt.want {
				t.Errorf("Settings() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestModel_EVT02_HIST03_InfoNamesTheProviderAndModelWithItsListPriceAndContextManagement(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name string
		want officina.ModelInfo
	}{
		{claude.Opus55, officina.ModelInfo{Provider: "anthropic", Name: claude.Opus55, Price: officina.Price{
			Input: 4, Output: 20, CacheRead: 0.20, CacheWrite: 5, CacheWriteHour: 8,
		}, Compacts: true, ClearsToolResults: true}},
		{"claude-from-the-future", officina.ModelInfo{Provider: "anthropic", Name: "claude-from-the-future",
			Compacts: true, ClearsToolResults: true}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			m, err := claude.New(tt.name, claude.EffortLow, claude.Options{})
			if err != nil {
				t.Fatalf("New() error = %v", err)
			}

			if diff := cmp.Diff(tt.want, m.Info()); diff != "" {
				t.Errorf("Info() mismatch (-want +got):\n%s", diff)
			}
		})
	}
}

func TestNew_MDL03_RejectsAnInvalidSetting(t *testing.T) {
	t.Parallel()
	tests := []struct {
		name   string
		model  string
		effort claude.Effort
		opts   claude.Options
		want   string
	}{
		{"no model", "", claude.EffortLow, claude.Options{}, "no model name"},
		{"no effort", claude.Opus55, "", claude.Options{}, `unknown effort ""`},
		{"unknown effort", claude.Opus55, "huge", claude.Options{}, `unknown effort "huge"`},
		{"negative max tokens", claude.Opus55, claude.EffortLow, claude.Options{MaxOutputTokens: -1}, "not positive"},
		{"unknown cache", claude.Opus55, claude.EffortLow, claude.Options{PrefixCache: "1d"}, "unknown cache lifetime"},
		{"prefix shorter than tail", claude.Opus55, claude.EffortLow,
			claude.Options{ConversationCache: claude.CacheOneHour}, "shorter than the conversation's"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			t.Parallel()
			_, err := claude.New(tt.model, tt.effort, tt.opts)

			if err == nil || !strings.Contains(err.Error(), tt.want) {
				t.Errorf("New() error = %v, want one saying %q", err, tt.want)
			}
		})
	}
}
