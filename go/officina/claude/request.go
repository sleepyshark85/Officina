package claude

import (
	"bytes"
	jsonv1 "encoding/json"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"fmt"

	"github.com/anthropics/anthropic-sdk-go"
	"github.com/anthropics/anthropic-sdk-go/packages/param"

	"github.com/sleepyshark85/officina/go/officina"
)

// params lays out one request: the tools in the order given (the core sorts them) with eager input streaming; the
// instructions as one system block holding the prefix's cache point; automatic caching for the conversation's tail;
// the context management, with its betas; then the messages, each written as JSON here so stored blocks go out byte
// for byte.
func (m *Model) params(req officina.Request) (anthropic.BetaMessageNewParams, error) {
	params := anthropic.BetaMessageNewParams{
		Model:        anthropic.Model(m.name),
		MaxTokens:    m.maxTokens,
		Thinking:     anthropic.BetaThinkingConfigParamUnion{OfAdaptive: &anthropic.BetaThinkingConfigAdaptiveParam{}},
		OutputConfig: anthropic.BetaOutputConfigParam{Effort: anthropic.BetaOutputConfigEffort(m.effort)},
		CacheControl: anthropic.BetaCacheControlEphemeralParam{TTL: anthropic.BetaCacheControlEphemeralTTL(m.tail)},
		System: []anthropic.BetaTextBlockParam{{
			Text:         req.Instructions,
			CacheControl: anthropic.BetaCacheControlEphemeralParam{TTL: anthropic.BetaCacheControlEphemeralTTL(m.prefix)},
		}},
		Tools:    make([]anthropic.BetaToolUnionParam, len(req.Tools)),
		Messages: make([]anthropic.BetaMessageParam, len(req.Messages)),
	}
	if req.MaxOutputTokens > 0 {
		params.MaxTokens = min(params.MaxTokens, req.MaxOutputTokens)
	}
	params.ContextManagement, params.Betas = contextManagement(req.ContextManagement)
	for i, t := range req.Tools {
		tool := &anthropic.BetaToolParam{
			Name:                t.Name,
			InputSchema:         param.Override[anthropic.BetaToolInputSchemaParam](t.InputSchema),
			EagerInputStreaming: anthropic.Bool(true),
		}
		if t.Description != "" {
			tool.Description = anthropic.String(t.Description)
		}
		params.Tools[i] = anthropic.BetaToolUnionParam{OfTool: tool}
	}
	for i, msg := range req.Messages {
		raw, err := message(msg)
		if err != nil {
			return anthropic.BetaMessageNewParams{}, fmt.Errorf("claude request: message %d: %w", i+1, err)
		}
		params.Messages[i] = param.Override[anthropic.BetaMessageParam](raw)
	}
	return params, nil
}

// contextManagement returns the context management of a request, with the betas it needs: tool-result clearing,
// then threshold compaction (never the on-demand kind, which has the client drop the compacted messages). An empty
// setting asks for nothing, so a request without context management has none and no betas.
func contextManagement(c officina.ContextManagement) (anthropic.BetaContextManagementConfigParam, []anthropic.AnthropicBeta) {
	var (
		config anthropic.BetaContextManagementConfigParam
		betas  []anthropic.AnthropicBeta
	)
	if t := c.ClearToolResults; t != (officina.ToolResultClearing{}) {
		clearing := &anthropic.BetaClearToolUses20250919EditParam{
			Trigger: anthropic.BetaClearToolUses20250919EditTriggerUnionParam{
				OfToolUses: &anthropic.BetaToolUsesTriggerParam{Value: int64(t.After)},
			},
			Keep: anthropic.BetaToolUsesKeepParam{Value: int64(t.Keep), Type: "tool_uses"},
		}
		if t.AtLeastTokens > 0 {
			clearing.ClearAtLeast = anthropic.BetaInputTokensClearAtLeastParam{Value: t.AtLeastTokens}
		}
		config.Edits = append(config.Edits, anthropic.BetaContextManagementConfigEditUnionParam{
			OfClearToolUses20250919: clearing,
		})
		betas = append(betas, anthropic.AnthropicBetaContextManagement2025_06_27)
	}
	if c.CompactAt > 0 {
		config.Edits = append(config.Edits, anthropic.BetaContextManagementConfigEditUnionParam{
			OfCompact20260112: &anthropic.BetaCompact20260112EditParam{
				Trigger: anthropic.BetaInputTokensTriggerParam{Value: c.CompactAt},
			},
		})
		betas = append(betas, anthropic.AnthropicBetaCompact2026_01_12)
	}
	return config, betas
}

// message writes a message as the API takes it. An operator message is a mid-conversation system message, its text
// the content; a block with raw JSON goes as it is, a tool result as a tool_result block, and any other is a text
// block. The result is in the canonical form, so the SDK, which compacts and escapes what it is given, sends it
// unchanged.
func message(msg officina.Message) (jsontext.Value, error) {
	type wire struct {
		Role    string `json:"role"`
		Content any    `json:"content"`
	}
	if msg.Role == officina.Operator {
		return marshal(wire{Role: "system", Content: msg.Text()})
	}
	content := make([]jsontext.Value, len(msg.Blocks))
	for i, b := range msg.Blocks {
		if b.Raw != nil {
			content[i] = b.Raw
			continue
		}
		if r := b.ToolResult; r != nil {
			result, err := marshal(struct {
				Type      string `json:"type"`
				ToolUseID string `json:"tool_use_id"`
				Content   string `json:"content"`
				IsError   bool   `json:"is_error,omitzero"`
			}{"tool_result", r.CallID, r.Content, r.IsError})
			if err != nil {
				return nil, err
			}
			content[i] = result
			continue
		}
		text, err := marshal(struct {
			Type string `json:"type"`
			Text string `json:"text"`
		}{"text", b.Text})
		if err != nil {
			return nil, err
		}
		content[i] = text
	}
	return marshal(wire{Role: string(msg.Role), Content: content})
}

// marshal writes v in the canonical form, keeping the escapes of the raw JSON it holds.
func marshal(v any) (jsontext.Value, error) {
	data, err := json.Marshal(v, jsontext.PreserveRawStrings(true), jsontext.EscapeForHTML(true),
		jsontext.EscapeForJS(true))
	if err != nil {
		return nil, fmt.Errorf("marshal: %w", err)
	}
	return data, nil
}

// canonical returns raw JSON in the form blocks are stored in: compact, with <, >, &, U+2028 and U+2029 escaped and
// every other escape as received. It is a fixed point of the SDK's encoder and of marshal.
func canonical(raw string) (jsontext.Value, error) {
	compact := jsontext.Value(raw).Clone()
	if err := compact.Compact(); err != nil {
		return nil, fmt.Errorf("compact a block: %w", err)
	}
	var escaped bytes.Buffer
	jsonv1.HTMLEscape(&escaped, compact)
	return escaped.Bytes(), nil
}
