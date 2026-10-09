// Command go-claude-features is spike G02: the S02 live check rerun on the Anthropic Go SDK's beta surface, streamed,
// with Opus 5.5. Throwaway code, not part of the solution.
//
//	go run . [all|offline|compact|clear|memory|updates|midsys|structured]
//
// The conversation is held as raw JSON messages ([]json.RawMessage). Each assistant block is stored as the accumulated
// block's RawJSON() and sent back through param.Override, so the SDK never re-encodes it. A middleware captures every
// request body, and each call checks that each stored message appears in it byte for byte.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"encoding/json/jsontext"
	jsonv2 "encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"time"

	"github.com/anthropics/anthropic-sdk-go"
	"github.com/anthropics/anthropic-sdk-go/option"
	"github.com/anthropics/anthropic-sdk-go/packages/param"
)

const model = "claude-opus-5-5"

// tap records the body of the last request sent.
type tap struct{ last []byte }

func (t *tap) middleware(req *http.Request, next option.MiddlewareNext) (*http.Response, error) {
	if req.Body != nil {
		b, err := io.ReadAll(req.Body)
		if err != nil {
			return nil, err
		}
		t.last = b
		req.Body = io.NopCloser(bytes.NewReader(b))
	}
	return next(req)
}

type spike struct {
	client  anthropic.Client
	tap     *tap
	ledger  ledger
	checked int
	bad     int
	// blocks stored, and how many of them normalizing changed
	blocks, normalized int
}

func main() {
	which := "all"
	if len(os.Args) > 1 {
		which = os.Args[1]
	}
	t := &tap{}
	s := &spike{
		client: anthropic.NewClient(option.WithMiddleware(t.middleware), option.WithRequestTimeout(10*time.Minute)),
		tap:    t,
	}
	ctx := context.Background()
	steps := []struct {
		name string
		run  func(context.Context) error
	}{
		{"offline", s.offline},
		{"compact", s.compact},
		{"clear", s.clear},
		{"memory", s.memory},
		{"updates", s.updates},
		{"midsys", s.midsys},
		{"structured", s.structured},
	}
	for _, st := range steps {
		if which != "all" && which != st.name {
			continue
		}
		fmt.Printf("\n===== %s =====\n", st.name)
		if err := st.run(ctx); err != nil {
			fmt.Printf("!! %s failed: %v\n", st.name, err)
		}
	}
	fmt.Printf("\nROUND TRIP: replayed messages checked=%d mismatched=%d; assistant blocks stored=%d changed by normalizing=%d\n",
		s.checked, s.bad, s.blocks, s.normalized)
	fmt.Printf("TOTAL: %s\n", &s.ledger)
}

// ---------- helpers

func userText(text string) json.RawMessage {
	return mustJSON(map[string]any{"role": "user", "content": text})
}

func toolResults(results [][2]string) json.RawMessage {
	content := make([]map[string]any, 0, len(results))
	for _, r := range results {
		content = append(content, map[string]any{"type": "tool_result", "tool_use_id": r[0], "content": r[1]})
	}
	return mustJSON(map[string]any{"role": "user", "content": content})
}

// assistant builds the assistant message from each block's accumulated raw JSON, normalized to the form the SDK
// sends a json.RawMessage in (see offline), and counts the blocks normalizing changed.
func (s *spike) assistant(m *anthropic.BetaMessage) json.RawMessage {
	raws := make([]string, 0, len(m.Content))
	for _, b := range m.Content {
		n := normalize(json.RawMessage(b.RawJSON()))
		s.blocks++
		if string(n) != b.RawJSON() {
			s.normalized++
			fmt.Printf("    normalizing changed a %s block: %s\n", b.Type, short(b.RawJSON(), 160))
		}
		raws = append(raws, string(n))
	}
	return json.RawMessage(`{"role":"assistant","content":[` + strings.Join(raws, ",") + `]}`)
}

func mustJSON(v any) json.RawMessage {
	b, err := json.Marshal(v)
	if err != nil {
		panic(err) // only fixed spike values are marshalled
	}
	return b
}

func short(s string, n int) string {
	s = strings.ReplaceAll(s, "\n", `\n`)
	if len(s) <= n {
		return s
	}
	return fmt.Sprintf("%s…(+%d)", s[:n], len(s)-n)
}

func summary(b anthropic.BetaContentBlockUnion) string {
	switch b.Type {
	case "thinking":
		return fmt.Sprintf("thinking(text=%dch, sig=%dch)", len(b.Thinking), len(b.Signature))
	case "text":
		return fmt.Sprintf("text(%q)", short(b.Text, 80))
	case "tool_use":
		return fmt.Sprintf("tool_use(%s %s)", b.Name, string(b.Input))
	case "compaction":
		return fmt.Sprintf("compaction(content=%dch)", len(b.Content.OfString))
	}
	return b.Type
}

// call makes one streamed request. params carries everything but the messages, which are raw JSON.
func (s *spike) call(ctx context.Context, label string, params anthropic.BetaMessageNewParams, msgs []json.RawMessage,
	onEvent func(anthropic.BetaRawMessageStreamEventUnion)) (*anthropic.BetaMessage, error) {
	params.Messages = make([]anthropic.BetaMessageParam, 0, len(msgs))
	for _, m := range msgs {
		params.Messages = append(params.Messages, param.Override[anthropic.BetaMessageParam](m))
	}
	var m anthropic.BetaMessage
	for attempt := 1; ; attempt++ {
		m = anthropic.BetaMessage{}
		stream := s.client.Beta.Messages.NewStreaming(ctx, params)
		events := 0
		for stream.Next() {
			ev := stream.Current()
			events++
			if err := m.Accumulate(ev); err != nil {
				return nil, fmt.Errorf("accumulate: %w", err)
			}
			if onEvent != nil {
				onEvent(ev)
			}
		}
		err := stream.Err()
		stream.Close()
		if err == nil {
			break
		}
		var apiErr *anthropic.Error
		if errors.As(err, &apiErr) || attempt >= 3 {
			return nil, err
		}
		fmt.Printf("  [%s] attempt %d: stream error after %d events (%v); retrying in 5s\n", label, attempt, events, err)
		time.Sleep(5 * time.Second)
	}

	// Each stored message must appear byte for byte in the body that went over the wire.
	checked, bad := 0, 0
	for _, msg := range msgs {
		checked++
		if !bytes.Contains(s.tap.last, msg) {
			bad++
			fmt.Printf("  !! message not byte-identical on the wire: %s\n", short(string(msg), 200))
		}
	}
	s.checked += checked
	s.bad += bad

	cost := s.ledger.add(m.Usage)
	blocks := make([]string, 0, len(m.Content))
	for _, b := range m.Content {
		blocks = append(blocks, summary(b))
	}
	fmt.Printf("  [%s] stop=%s blocks=[%s]\n", label, m.StopReason, strings.Join(blocks, ", "))
	fmt.Printf("  [%s] usage in=%d out=%d cache_read=%d cache_write=%d | call $%.4f running $%.4f | replayed msgs checked=%d mismatched=%d\n",
		label, m.Usage.InputTokens, m.Usage.OutputTokens, m.Usage.CacheReadInputTokens, m.Usage.CacheCreationInputTokens,
		cost, s.ledger.total, checked, bad)
	if len(m.Usage.Iterations) > 0 {
		its := make([]string, 0, len(m.Usage.Iterations))
		for _, it := range m.Usage.Iterations {
			its = append(its, fmt.Sprintf("{%s in=%d cache_read=%d cache_write=%d out=%d}",
				it.Type, it.InputTokens, it.CacheReadInputTokens, it.CacheCreationInputTokens, it.OutputTokens))
		}
		fmt.Printf("  [%s] usage.iterations (typed)=%s\n", label, strings.Join(its, " "))
	}
	if m.ContextManagement.JSON.AppliedEdits.Valid() {
		edits := make([]string, 0, len(m.ContextManagement.AppliedEdits))
		for _, e := range m.ContextManagement.AppliedEdits {
			edits = append(edits, fmt.Sprintf("{%s cleared_input_tokens=%d cleared_tool_uses=%d}", e.Type, e.ClearedInputTokens, e.ClearedToolUses))
		}
		fmt.Printf("  [%s] context_management.applied_edits (typed)=[%s] raw=%s\n", label, strings.Join(edits, " "), m.ContextManagement.RawJSON())
	}
	return &m, nil
}

// instructions are long enough to pass the cache minimum, frozen, and the same as the .NET spike's.
func instructions() string {
	var b strings.Builder
	b.WriteString("You are the assistant of a small bookshop called «Café Libro». Answer briefly and precisely.\n")
	for i := 1; i <= 60; i++ {
		if i > 1 {
			b.WriteString("\n")
		}
		fmt.Fprintf(&b, "Policy %d: when a customer asks about topic %d, check the catalog first, never invent stock levels, and quote prices in euros.", i, i)
	}
	return b.String()
}

func system() []anthropic.BetaTextBlockParam {
	return []anthropic.BetaTextBlockParam{{Text: instructions(), CacheControl: anthropic.NewBetaCacheControlEphemeralParam()}}
}

func effort(e anthropic.BetaOutputConfigEffort) anthropic.BetaOutputConfigParam {
	return anthropic.BetaOutputConfigParam{Effort: e}
}

func fieldJSON(v any) string { return string(mustJSON(v)) }

// ---------- 0: offline replay check against a local server (no API cost)

func (s *spike) offline(ctx context.Context) error {
	var body []byte
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ = io.ReadAll(r.Body)
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, `{"id":"msg_x","type":"message","role":"assistant","model":"m","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}`)
	}))
	defer srv.Close()
	c := anthropic.NewClient(option.WithBaseURL(srv.URL), option.WithAPIKey("offline"))
	// Bytes a re-encoder would change: \u escapes, literal UTF-8, HTML characters, an escaped slash, key order, spacing.
	raw := json.RawMessage(`{"role":"assistant","content":[{"type":"text","text":"Caf\u00e9 «Muñoz» <b>&amp;</b> a\/b +x"} , {"text":"z","type":"text"}]}`)
	_, err := c.Beta.Messages.New(ctx, anthropic.BetaMessageNewParams{
		Model: model, MaxTokens: 10,
		Messages: []anthropic.BetaMessageParam{
			param.Override[anthropic.BetaMessageParam](userText("hi")),
			param.Override[anthropic.BetaMessageParam](raw),
		},
	})
	if err != nil {
		return err
	}
	fmt.Printf("  A param.Override, sent:   %s\n", raw)
	fmt.Printf("  A param.Override, wire:   %s\n", body)
	fmt.Printf("  A raw message byte-identical on the wire: %v\n", bytes.Contains(body, raw))
	norm := normalize(raw)
	fmt.Printf("  A normalized (json.Compact + json.HTMLEscape) form: %s\n", norm)
	fmt.Printf("  A normalized form byte-identical on the wire: %v; normalizing twice changes nothing: %v\n",
		bytes.Contains(body, norm), bytes.Equal(normalize(norm), norm))

	// B: the whole body as bytes, streamed; the SDK still adds "stream":true and the beta header from params.
	whole := []byte(`{"model":"claude-opus-5-5","max_tokens":10,"messages":[{"role":"user","content":"hi"},` + string(raw) + `]}`)
	stream := c.Beta.Messages.NewStreaming(ctx, anthropic.BetaMessageNewParams{
		Betas: []anthropic.AnthropicBeta{anthropic.AnthropicBetaCompact2026_01_12},
	}, option.WithRequestBody("application/json", whole), option.WithMiddleware(func(r *http.Request, next option.MiddlewareNext) (*http.Response, error) {
		fmt.Printf("  B anthropic-beta header: %q\n", r.Header.Get("anthropic-beta"))
		return next(r)
	}))
	for stream.Next() {
	}
	_ = stream.Close() // the fake server answers JSON, not SSE; only the request matters here
	fmt.Printf("  B WithRequestBody, wire: %s\n", body)
	fmt.Printf("  B raw message byte-identical on the wire: %v\n", bytes.Contains(body, raw))

	// C: the block held as a jsontext.Value (encoding/json/v2), as the core will hold it.
	block := jsontext.Value(`{"type":"text", "text":"Caf\u00e9 «Muñoz» <b>&amp;</b> a\/b +x"}`)
	type message struct {
		Role    string           `json:"role"`
		Content []jsontext.Value `json:"content"`
	}
	v2, err := jsonv2.Marshal(message{Role: "assistant", Content: []jsontext.Value{block}})
	if err != nil {
		return err
	}
	fmt.Printf("  C jsonv2.Marshal of a message holding the jsontext.Value: %s\n", v2)
	fmt.Printf("  C block byte-identical in the v2 output: %v\n", bytes.Contains(v2, block))
	v2p, err := jsonv2.Marshal(message{Role: "assistant", Content: []jsontext.Value{block}}, jsontext.PreserveRawStrings(true))
	if err != nil {
		return err
	}
	fmt.Printf("  C jsonv2.Marshal with jsontext.PreserveRawStrings(true): %s\n", v2p)
	compacted := block.Clone()
	if err := compacted.Compact(); err != nil {
		return err
	}
	fmt.Printf("  C block after jsontext.Value.Compact(): %s; byte-identical in the PreserveRawStrings output: %v\n",
		compacted, bytes.Contains(v2p, compacted))
	v1, _ := json.Marshal(message{Role: "assistant", Content: []jsontext.Value{block}})
	fmt.Printf("  C encoding/json (v1 API) Marshal of the same: %s\n", v1)
	// C through the SDK: the v2-built message handed to param.Override.
	_, err = c.Beta.Messages.New(ctx, anthropic.BetaMessageNewParams{
		Model: model, MaxTokens: 10,
		Messages: []anthropic.BetaMessageParam{
			param.Override[anthropic.BetaMessageParam](userText("hi")),
			param.Override[anthropic.BetaMessageParam](jsontext.Value(v2)),
		},
	})
	if err != nil {
		return err
	}
	fmt.Printf("  C via param.Override(jsontext.Value), wire: %s\n", body)
	fmt.Printf("  C block byte-identical on the wire: %v\n", bytes.Contains(body, block))

	// The typed form, for comparison: what the SDK writes for the same text.
	typed := anthropic.BetaMessageParam{Role: anthropic.BetaMessageParamRoleAssistant,
		Content: []anthropic.BetaContentBlockParamUnion{anthropic.NewBetaTextBlock("Café «Muñoz» <b>&amp;</b> a/b +x")}}
	fmt.Printf("  typed param marshals as: %s\n", fieldJSON(typed))
	return nil
}

// normalize puts raw JSON into the form encoding/json writes a json.RawMessage in: compact, with <, > and & escaped.
// Stored in that form, a message goes over the wire unchanged.
func normalize(raw json.RawMessage) json.RawMessage {
	var compact bytes.Buffer
	if err := json.Compact(&compact, raw); err != nil {
		panic(err) // the API and this spike only produce valid JSON
	}
	var escaped bytes.Buffer
	json.HTMLEscape(&escaped, compact.Bytes())
	return escaped.Bytes()
}

// ---------- 1: server-side compaction (compact_20260112)

func catalog(n int) string {
	genres := []string{"mystery", "history", "poetry", "science", "travel", "cooking"}
	lines := make([]string, 0, n)
	for i := 1; i <= n; i++ {
		lines = append(lines, fmt.Sprintf("Item %04d: \"Volume %d of the %s series\" by Author %d, shelf %c%d, price %d.%02d EUR.",
			i, i, genres[i%6], i*7%997, rune('A'+i%26), i%40, 5+i%30, i%100))
	}
	return strings.Join(lines, "\n")
}

func (s *spike) compact(ctx context.Context) error {
	count, err := s.client.Beta.Messages.CountTokens(ctx, anthropic.BetaMessageCountTokensParams{
		Model:    model,
		System:   anthropic.BetaMessageCountTokensParamsSystemUnion{OfBetaTextBlockArray: system()},
		Messages: []anthropic.BetaMessageParam{anthropic.NewBetaUserMessage(anthropic.NewBetaTextBlock(catalog(2000)))},
	})
	if err != nil {
		return fmt.Errorf("count tokens: %w", err)
	}
	lines := int(2000 * 52_000.0 / float64(count.InputTokens))
	fmt.Printf("count_tokens: 2000 lines = %d tokens -> using %d lines (~52k)\n", count.InputTokens, lines)

	p := anthropic.BetaMessageNewParams{
		Model:        model,
		MaxTokens:    8000,
		Betas:        []anthropic.AnthropicBeta{anthropic.AnthropicBetaCompact2026_01_12},
		System:       system(),
		CacheControl: anthropic.NewBetaCacheControlEphemeralParam(),
		OutputConfig: effort(anthropic.BetaOutputConfigEffortLow),
		ContextManagement: anthropic.BetaContextManagementConfigParam{
			Edits: []anthropic.BetaContextManagementConfigEditUnionParam{{
				OfCompact20260112: &anthropic.BetaCompact20260112EditParam{Trigger: anthropic.BetaInputTokensTriggerParam{Value: 50_000}},
			}},
		},
	}
	fmt.Println("context_management json: " + fieldJSON(p.ContextManagement))

	msgs := []json.RawMessage{userText(catalog(lines) + "\n\nWhat is the title of item 0042? Reply with the title only.")}
	deltas := 0
	m1, err := s.call(ctx, "compact#1", p, msgs, func(ev anthropic.BetaRawMessageStreamEventUnion) {
		switch ev.Type {
		case "content_block_start":
			if ev.ContentBlock.Type == "compaction" {
				fmt.Println("  stream: content_block_start compaction " + short(ev.ContentBlock.RawJSON(), 120))
			}
		case "content_block_delta":
			if ev.Delta.Type == "compaction_delta" {
				deltas++
			}
		}
	})
	if err != nil {
		return err
	}
	fmt.Printf("  stream: compaction_delta events=%d\n", deltas)
	for _, b := range m1.Content {
		if c, ok := b.AsAny().(anthropic.BetaCompactionBlock); ok {
			fmt.Printf("  compaction block typed: content=%dch encrypted_content=%dch\n", len(c.Content), len(c.EncryptedContent))
			fmt.Println("  compaction block raw (first 400 chars): " + short(b.RawJSON(), 400))
		}
	}
	fmt.Println("  usage.iterations raw: " + m1.Usage.JSON.Iterations.Raw())

	msgs = append(msgs, s.assistant(m1), userText("Which shelf is item 0042 on? Answer briefly."))
	m2, err := s.call(ctx, "compact#2", p, msgs, nil)
	if err != nil {
		return err
	}
	msgs = append(msgs, s.assistant(m2), userText("And its price? Answer briefly."))
	_, err = s.call(ctx, "compact#3", p, msgs, nil)
	return err
}

// ---------- 2: tool-result clearing (clear_tool_uses_20250919)

func lookup(id int) string {
	var b strings.Builder
	fmt.Fprintf(&b, "Book %d: \"The Quiet Harbour, part %d\" by Mara Linde.", id, id)
	for i := 1; i <= 70; i++ {
		fmt.Fprintf(&b, "\nReview %d of book %d: readers praised chapter %d for its pacing, its setting and the careful translation.", i, id, i)
	}
	return b.String()
}

// toolUses returns the tool_use blocks as the union type, whose Input is json.RawMessage
// (the BetaToolUseBlock variant decodes Input into an any).
func toolUses(m *anthropic.BetaMessage) []anthropic.BetaContentBlockUnion {
	var uses []anthropic.BetaContentBlockUnion
	for _, b := range m.Content {
		if b.Type == "tool_use" {
			uses = append(uses, b)
		}
	}
	return uses
}

func (s *spike) clear(ctx context.Context) error {
	p := anthropic.BetaMessageNewParams{
		Model:        model,
		MaxTokens:    4000,
		Betas:        []anthropic.AnthropicBeta{anthropic.AnthropicBetaContextManagement2025_06_27},
		System:       system(),
		CacheControl: anthropic.NewBetaCacheControlEphemeralParam(),
		OutputConfig: effort(anthropic.BetaOutputConfigEffortLow),
		Tools: []anthropic.BetaToolUnionParam{{OfTool: &anthropic.BetaToolParam{
			Name:        "lookup_book",
			Description: anthropic.String("Look up one book by id. Returns its record and reviews."),
			InputSchema: anthropic.BetaToolInputSchemaParam{
				Properties: map[string]any{"id": map[string]any{"type": "integer", "description": "Book id"}},
				Required:   []string{"id"},
			},
		}}},
		ToolChoice: anthropic.BetaToolChoiceUnionParam{OfAuto: &anthropic.BetaToolChoiceAutoParam{DisableParallelToolUse: anthropic.Bool(true)}},
		ContextManagement: anthropic.BetaContextManagementConfigParam{
			Edits: []anthropic.BetaContextManagementConfigEditUnionParam{{
				OfClearToolUses20250919: &anthropic.BetaClearToolUses20250919EditParam{
					Trigger: anthropic.BetaClearToolUses20250919EditTriggerUnionParam{OfToolUses: &anthropic.BetaToolUsesTriggerParam{Value: 2}},
					Keep:    anthropic.BetaToolUsesKeepParam{Value: 1},
				},
			}},
		},
	}
	fmt.Println("context_management json: " + fieldJSON(p.ContextManagement))
	msgs := []json.RawMessage{userText("Look up books 1, 2, 3 and 4 with lookup_book, one call per reply, in that order. Then tell me the author.")}
	for i := 1; i <= 6; i++ {
		m, err := s.call(ctx, fmt.Sprintf("clear#%d", i), p, msgs, nil)
		if err != nil {
			return err
		}
		msgs = append(msgs, s.assistant(m))
		if m.StopReason != anthropic.BetaStopReasonToolUse {
			break
		}
		var results [][2]string
		for _, u := range toolUses(m) {
			var in struct{ ID int }
			if err := json.Unmarshal(u.Input, &in); err != nil {
				return err
			}
			results = append(results, [2]string{u.ID, lookup(in.ID)})
		}
		msgs = append(msgs, toolResults(results))
	}
	return nil
}

// ---------- 3: memory tool (memory_20250818), answered client-side

type memoryInput struct {
	Command    string `json:"command"`
	Path       string `json:"path"`
	FileText   string `json:"file_text"`
	OldStr     string `json:"old_str"`
	NewStr     string `json:"new_str"`
	InsertLine int    `json:"insert_line"`
	InsertText string `json:"insert_text"`
	OldPath    string `json:"old_path"`
	NewPath    string `json:"new_path"`
}

func handleMemory(store map[string]string, in memoryInput) string {
	switch in.Command {
	case "view":
		if f, ok := store[in.Path]; ok {
			var b strings.Builder
			for i, l := range strings.Split(f, "\n") {
				fmt.Fprintf(&b, "%6d\t%s\n", i+1, l)
			}
			return b.String()
		}
		var b strings.Builder
		fmt.Fprintf(&b, "Here are the files and directories up to 2 levels deep in %s:", in.Path)
		for k, v := range store {
			if strings.HasPrefix(k, strings.TrimSuffix(in.Path, "/")+"/") {
				fmt.Fprintf(&b, "\n%d\t%s", len(v), k)
			}
		}
		return b.String()
	case "create":
		store[in.Path] = in.FileText
		return "File created successfully at: " + in.Path
	case "str_replace":
		f, ok := store[in.Path]
		if !ok {
			return "Error: The path " + in.Path + " does not exist."
		}
		store[in.Path] = strings.Replace(f, in.OldStr, in.NewStr, 1)
		return "The memory file has been edited."
	case "insert":
		f, ok := store[in.Path]
		if !ok {
			return "Error: The path " + in.Path + " does not exist."
		}
		ls := strings.Split(f, "\n")
		at := min(max(in.InsertLine, 0), len(ls))
		ls = append(ls[:at], append([]string{in.InsertText}, ls[at:]...)...)
		store[in.Path] = strings.Join(ls, "\n")
		return "The file " + in.Path + " has been edited."
	case "delete":
		delete(store, in.Path)
		return "Successfully deleted " + in.Path
	case "rename":
		store[in.NewPath] = store[in.OldPath]
		delete(store, in.OldPath)
		return "Renamed."
	}
	return "Error: unknown command " + in.Command
}

func (s *spike) memory(ctx context.Context) error {
	store := map[string]string{}
	p := anthropic.BetaMessageNewParams{
		Model:        model,
		MaxTokens:    4000,
		System:       system(),
		OutputConfig: effort(anthropic.BetaOutputConfigEffortMedium),
		Tools:        []anthropic.BetaToolUnionParam{{OfMemoryTool20250818: &anthropic.BetaMemoryTool20250818Param{}}},
	}
	fmt.Println("tools json: " + fieldJSON(p.Tools))
	conversation := func(label, prompt string) error {
		msgs := []json.RawMessage{userText(prompt)}
		for i := 1; i <= 6; i++ {
			m, err := s.call(ctx, fmt.Sprintf("%s#%d", label, i), p, msgs, nil)
			if err != nil {
				return err
			}
			msgs = append(msgs, s.assistant(m))
			if m.StopReason != anthropic.BetaStopReasonToolUse {
				return nil
			}
			var results [][2]string
			for _, u := range toolUses(m) {
				var in memoryInput
				if err := json.Unmarshal(u.Input, &in); err != nil {
					return err
				}
				r := handleMemory(store, in)
				fmt.Printf("    memory %s -> %s\n", string(u.Input), short(r, 120))
				results = append(results, [2]string{u.ID, r})
			}
			msgs = append(msgs, toolResults(results))
		}
		return nil
	}
	if err := conversation("memA", "Hi, I'm Ana. For future conversations, please remember that I love mystery novels and dislike horror."); err != nil {
		return err
	}
	fmt.Println("  store after A: " + fieldJSON(store))
	return conversation("memB", "Hello again. Which genre should I browse today? One sentence.")
}

// ---------- 4: thinking display "updates" in a tool loop

func (s *spike) updates(ctx context.Context) error {
	titleSchema := anthropic.BetaToolInputSchemaParam{
		Properties: map[string]any{"title": map[string]any{"type": "string"}},
		Required:   []string{"title"},
	}
	p := anthropic.BetaMessageNewParams{
		Model:        model,
		MaxTokens:    4000,
		Betas:        []anthropic.AnthropicBeta{anthropic.AnthropicBetaThinkingDisplayUpdates2026_08_18},
		System:       system(),
		CacheControl: anthropic.NewBetaCacheControlEphemeralParam(),
		Thinking: anthropic.BetaThinkingConfigParamUnion{OfAdaptive: &anthropic.BetaThinkingConfigAdaptiveParam{
			Display: anthropic.BetaThinkingConfigAdaptiveDisplayUpdates,
		}},
		OutputConfig: effort(anthropic.BetaOutputConfigEffortMedium),
		Tools: []anthropic.BetaToolUnionParam{
			{OfTool: &anthropic.BetaToolParam{Name: "check_stock", Description: anthropic.String("Number of copies in stock for a title."), InputSchema: titleSchema}},
			{OfTool: &anthropic.BetaToolParam{Name: "get_price", Description: anthropic.String("Price in euros for a title."), InputSchema: titleSchema}},
		},
	}
	fmt.Println("thinking json: " + fieldJSON(p.Thinking))
	msgs := []json.RawMessage{userText(`For "The Name of the Rose", "Gaudy Night" and "The Daughter of Time": check stock and price of each, ` +
		"one title at a time, and keep me posted on what you are doing between the lookups. Then tell me which in-stock title is cheapest.")}
	for i := 1; i <= 8; i++ {
		var deltas strings.Builder
		thinkingDeltas, sigDeltas := 0, 0
		m, err := s.call(ctx, fmt.Sprintf("updates#%d", i), p, msgs, func(ev anthropic.BetaRawMessageStreamEventUnion) {
			if ev.Type != "content_block_delta" {
				return
			}
			switch d := ev.AsContentBlockDelta().Delta.AsAny().(type) {
			case anthropic.BetaThinkingDelta:
				thinkingDeltas++
				deltas.WriteString("[" + d.Thinking + "]")
			case anthropic.BetaSignatureDelta:
				sigDeltas++
			}
		})
		if err != nil {
			return err
		}
		fmt.Printf("    stream: thinking_delta=%d signature_delta=%d deltas=%s\n", thinkingDeltas, sigDeltas, short(deltas.String(), 300))
		for _, b := range m.Content {
			if b.Type == "thinking" {
				fmt.Println("    thinking block: " + short(b.RawJSON(), 260))
			}
		}
		msgs = append(msgs, s.assistant(m))
		if m.StopReason != anthropic.BetaStopReasonToolUse {
			break
		}
		var results [][2]string
		for _, u := range toolUses(m) {
			var in struct{ Title string }
			if err := json.Unmarshal(u.Input, &in); err != nil {
				return err
			}
			r := fmt.Sprintf("%d.90 EUR", 10+len(in.Title)%7)
			if u.Name == "check_stock" {
				r = fmt.Sprintf("%d copies", len(in.Title)%4)
			}
			results = append(results, [2]string{u.ID, r})
		}
		msgs = append(msgs, toolResults(results))
	}
	return nil
}

// ---------- 5: mid-conversation system message carrying run context, with caching

func (s *spike) midsys(ctx context.Context) error {
	p := anthropic.BetaMessageNewParams{
		Model:        model,
		MaxTokens:    2000,
		System:       system(),
		CacheControl: anthropic.NewBetaCacheControlEphemeralParam(),
		OutputConfig: effort(anthropic.BetaOutputConfigEffortLow),
	}
	// The typed constructor, stored as the JSON it marshals to.
	sys := mustJSON(anthropic.NewBetaSystemMessage(anthropic.BetaSystemMessageOutputConfigParam{},
		anthropic.NewBetaTextBlock("Run context: today is 2026-10-09; the customer is Ana Muñoz (loyalty member).")))
	fmt.Println("  typed system message marshals as: " + string(sys))
	msgs := []json.RawMessage{userText("Hi! Greet me by name and tell me today's date."), sys}
	m1, err := s.call(ctx, "midsys#1", p, msgs, nil)
	if err != nil {
		return err
	}
	body := string(s.tap.last)
	fmt.Println("    request messages: " + short(body[strings.Index(body, `"messages"`):], 300))
	fmt.Println("    stored text block raw: " + short(m1.Content[len(m1.Content)-1].RawJSON(), 200))
	msgs = append(msgs, s.assistant(m1), userText("Recommend one mystery novel, in one sentence."), sys)
	m2, err := s.call(ctx, "midsys#2", p, msgs, nil)
	if err != nil {
		return err
	}
	msgs = append(msgs, s.assistant(m2), userText("And one more, also one sentence."), sys)
	_, err = s.call(ctx, "midsys#3", p, msgs, nil)
	return err
}

// ---------- 6: structured output

// subsetSchema is what the core's validator subset (types, properties, required, closed objects, items, anyOf,
// enum, const, pattern, length and item bounds, minimum and maximum, annotations) produces for a book pick.
func subsetSchema(withBounds bool) map[string]any {
	year := map[string]any{"type": "integer", "description": "Year of first publication"}
	tags := map[string]any{"type": "array", "items": map[string]any{"type": "string"}, "minItems": 1}
	if withBounds {
		year["minimum"] = 1400
		year["maximum"] = 2026
		tags["maxItems"] = 5
	}
	return map[string]any{
		"$schema": "https://json-schema.org/draft/2020-12/schema",
		"type":    "object",
		"properties": map[string]any{
			"title":  map[string]any{"type": "string", "minLength": 1},
			"author": map[string]any{"type": "string"},
			"year":   year,
			"price":  map[string]any{"type": "number"},
			"genre":  map[string]any{"enum": []string{"Mystery", "Horror", "History"}},
			"tags":   tags,
			"isbn":   map[string]any{"anyOf": []any{map[string]any{"type": "string", "pattern": `^[0-9-]{10,17}$`}, map[string]any{"type": "null"}}},
			"note": map[string]any{"type": []string{"object", "null"}, "properties": map[string]any{"text": map[string]any{"type": "string"}},
				"required": []string{"text"}, "additionalProperties": false},
		},
		"required":             []string{"title", "author", "year", "price", "genre", "tags", "isbn", "note"},
		"additionalProperties": false,
	}
}

// bookPick is the same shape as a Go struct, for the SDK's own schema generator.
type bookPick struct {
	Title  string   `json:"title"`
	Author string   `json:"author"`
	Year   int      `json:"year" jsonschema:"minimum=1400,maximum=2026"`
	Price  float64  `json:"price"`
	Genre  string   `json:"genre" jsonschema:"enum=Mystery,enum=Horror,enum=History"`
	Tags   []string `json:"tags" jsonschema:"maxItems=5"`
	ISBN   *string  `json:"isbn"`
	Note   *struct {
		Text string `json:"text"`
	} `json:"note"`
}

func (s *spike) structured(ctx context.Context) error {
	try := func(label string, format anthropic.BetaJSONOutputFormatParam) {
		p := anthropic.BetaMessageNewParams{
			Model:        model,
			MaxTokens:    2000,
			OutputConfig: anthropic.BetaOutputConfigParam{Effort: anthropic.BetaOutputConfigEffortLow, Format: format},
		}
		wire, _ := json.Marshal(p.OutputConfig)
		fmt.Printf("  %s output_config on the wire: %s\n", label, short(string(wire), 900))
		m, err := s.call(ctx, label, p, []json.RawMessage{userText("Pick one mystery novel for Ana.")}, nil)
		if err != nil {
			var apiErr *anthropic.Error
			if errors.As(err, &apiErr) {
				fmt.Printf("    %s REJECTED %d: %s\n", label, apiErr.StatusCode, short(apiErr.RawJSON(), 400))
				return
			}
			fmt.Printf("    %s failed: %v\n", label, err)
			return
		}
		var pick bookPick
		perr := m.ParseOutput(&pick)
		fmt.Printf("    %s accepted; output: %s; ParseOutput into struct: err=%v\n", label, short(m.Content[len(m.Content)-1].Text, 300), perr)
	}
	try("subset", anthropic.BetaJSONOutputFormatParam{Schema: subsetSchema(false)})
	try("subset-with-bounds", anthropic.BetaJSONOutputFormatParam{Schema: subsetSchema(true)})
	try("sdk-transformed-map", anthropic.BetaJSONSchemaOutputFormat(subsetSchema(true)))
	// Offline: the same helper on the schema without the one `"type": [..., "null"]` keyword.
	noTypeArray := subsetSchema(true)
	delete(noTypeArray["properties"].(map[string]any), "note")
	fmt.Println("  offline: BetaJSONSchemaOutputFormat without the type array: " + fieldJSON(anthropic.BetaJSONSchemaOutputFormat(noTypeArray).Schema))
	try("sdk-from-struct", anthropic.BetaJSONOutputFormatParam{Schema: &bookPick{}})
	return nil
}

// ---------- cost

// ledger prices Opus 5.5: $4 input, $20 output, cache read $0.20, 5m write 1.25x, 1h write 2x (per MTok).
// When usage.iterations is present, the top-level counts cover only the message iteration, so sum the iterations.
type ledger struct {
	in, out, read, w5, w1 int64
	total                 float64
}

func (l *ledger) add(u anthropic.BetaUsage) float64 {
	type part struct{ in, out, read, w5, w1, w int64 }
	parts := []part{{u.InputTokens, u.OutputTokens, u.CacheReadInputTokens,
		u.CacheCreation.Ephemeral5mInputTokens, u.CacheCreation.Ephemeral1hInputTokens, u.CacheCreationInputTokens}}
	if len(u.Iterations) > 0 {
		parts = parts[:0]
		for _, it := range u.Iterations {
			parts = append(parts, part{it.InputTokens, it.OutputTokens, it.CacheReadInputTokens,
				it.CacheCreation.Ephemeral5mInputTokens, it.CacheCreation.Ephemeral1hInputTokens, it.CacheCreationInputTokens})
		}
	}
	cost := 0.0
	for _, p := range parts {
		w5, w1 := p.w5, p.w1
		if w5+w1 == 0 {
			w5 = p.w
		}
		l.in += p.in
		l.out += p.out
		l.read += p.read
		l.w5 += w5
		l.w1 += w1
		cost += (float64(p.in)*4 + float64(p.out)*20 + float64(p.read)*0.20 + float64(w5)*5 + float64(w1)*8) / 1e6
	}
	l.total += cost
	return cost
}

func (l *ledger) String() string {
	return fmt.Sprintf("input=%d output=%d cache_read=%d write5m=%d write1h=%d cost=$%.4f", l.in, l.out, l.read, l.w5, l.w1, l.total)
}
