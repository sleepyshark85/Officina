# Spike Go S02: Claude features live check on the Go SDK

**Date:** 2026-10-09 · **SDK:** `github.com/anthropics/anthropic-sdk-go` v1.79.1 (latest), Go 1.27.2 · **Model:**
`claude-opus-5-5`, beta surface only (`client.Beta.Messages`), streamed · **Code:**
[`spikes/go-claude-features/main.go`](../../../spikes/go-claude-features/main.go) · **Output:**
[`spikes/go-claude-features/run-output.txt`](../../../spikes/go-claude-features/run-output.txt) · **API cost:** about
$0.47 (compaction alone $0.26)

Reruns the .NET spike S02 ([`docs/spikes/claude-features.md`](../../../docs/spikes/claude-features.md)) on the Go SDK,
with the same prompts, catalog and thresholds, so the numbers compare. The spike is its own module, not part of `go/`.
How it holds the conversation: messages are raw JSON; each assistant block is stored as the accumulated block's
`RawJSON()` and sent back with `param.Override[anthropic.BetaMessageParam](raw)`. Everything else in the request is
the typed `BetaMessageNewParams`. An `option.WithMiddleware` captures each request body, and every call checks that
each stored message appears in it byte for byte. An `offline` step does the same against an `httptest.Server` with
bytes chosen to break a re-encoder (no API cost).

**Verdict:** all seven features work on the Go SDK, and **every one is typed**: no feature needs `option.WithJSONSet`
or a raw request field. Results match .NET call for call. One caveat changes Go S04 and G9: the SDK sends a raw
message through its own fork of `encoding/json` (`internal/encoding/json`, v1 behaviour), which compacts it and escapes `<`, `>` and `&`, so stored bytes are
replayed unchanged only if they are stored in that form (or the request body is built by us). `encoding/json/v2`
does not write a `jsontext.Value` verbatim by default either.

## Findings

| # | Feature | Status | Typed in the Go SDK? | Evidence | Consequence |
|---|---|---|---|---|---|
| 1 | Server-side compaction (`compact_20260112`, beta `compact-2026-01-12`) | **Works, with the .NET caveats** | Yes: `ContextManagement.Edits[].OfCompact20260112` with `BetaInputTokensTriggerParam`, beta constant `AnthropicBetaCompact2026_01_12`; response block `BetaCompactionBlock`; stream `content_block_start` (type `compaction`, `"content":null`) then one `compaction_delta`, both handled by `Accumulate`. The newer on-demand form (`Compaction BetaCompactionConfigUnionParam`, beta `compact-2026-09-04`) is typed too, not tested | Trigger 50,000 on a 52k prompt: `[compaction(343ch), thinking, text]`, `end_turn`. `usage.iterations` (typed, `Usage.Iterations[i].Type/InputTokens/…`) = `{compaction in 48, cache_read 2615, cache_write 50090, out 160} {message in 2, cache_read 2615, cache_write 210, out 32}`; top-level usage is the message iteration only. `applied_edits` stayed `[]`. Calls 2 and 3 replayed the compaction block and read 2615 and 2879 from cache; answers right (shelf Q2, 17.42 €) | Go S10 as S10: cost and report compaction from `Usage.Iterations`. After `Accumulate`, `Usage.JSON.Iterations.Raw()` is empty (the typed slice is set, its raw metadata is not); read the typed fields, or the message's `RawJSON()` |
| 2 | Tool-result clearing (`clear_tool_uses_20250919`, beta `context-management-2025-06-27`) | **Works, with the .NET caveat** | Yes: `OfClearToolUses20250919` with `Trigger.OfToolUses`, `Keep`, `ClearAtLeast`; response `ContextManagement.AppliedEdits[i].ClearedInputTokens/ClearedToolUses`, filled from `message_delta` by `Accumulate` | Trigger 2 tool uses, keep 1: first applied on call 4 (`4892` tokens, `2` uses), then call 5 (`4799`, `2`), as in .NET. Call 4's cache read fell back to the system prompt (3035) | Go S10: report the typed `AppliedEdits`; set `clear_at_least` outside demo mode, as for .NET |
| 3 | Memory tool (`memory_20250818`) | **Works** | Yes: `BetaToolUnionParam{OfMemoryTool20250818: &anthropic.BetaMemoryTool20250818Param{}}` → `{"name":"memory","type":"memory_20250818"}`, no beta header | A: `view /memories` → `create /memories/customers/ana.md`. B (fresh): `view` → `view` file → answer used the note ("If you're Ana…"). Prefix cache write 4269 (vs 2671 without the tool) | Go S09 as S09. Read tool input from `BetaContentBlockUnion.Input` (`json.RawMessage`): the typed `BetaToolUseBlock.Input` is `any`, already decoded |
| 4 | Thinking `display: "updates"` (beta `thinking-display-updates-2026-08-18`) in a tool loop | **Works, with the .NET caveat** | Yes: `BetaThinkingConfigAdaptiveParam{Display: BetaThinkingConfigAdaptiveDisplayUpdates}`; deltas as `BetaThinkingDelta` / `BetaSignatureDelta` via `ev.AsContentBlockDelta().Delta.AsAny()` | Thinking blocks only on the first and last turns, each `"thinking":""` from one empty `thinking_delta` plus one `signature_delta`. Progress came as `text` blocks before `tool_use` ("I'll start with…") | Go S04/Go S06: thinking text optional and possibly empty; show text blocks between tool calls as progress |
| 5 | Mid-conversation `system` message for run context, cache point on the last system block plus top-level automatic caching | **Works** | Yes: `BetaMessageParamRoleSystem`, `anthropic.NewBetaSystemMessage(…)`, plus `ClearAt` and per-message `OutputConfig` | Typed constructor marshals to `{"content":[{"text":"Run context: …","type":"text"}],"role":"system"}` (blocks, not a string; accepted). Model used both facts. Cache: call 1 wrote 2671; call 2 read 2671 / wrote 155; call 3 read 2826 / wrote 167 | Go S04 as designed (CTX-02, CTX-03) |
| 6 | Structured output with a schema the core's validator subset produces | **Works, with adjustments** | Yes: `OutputConfig.Format = BetaJSONOutputFormatParam{Schema: …}`; `Schema` takes a `map[string]any`, a `json.RawMessage` or a struct pointer; `BetaMessage.ParseOutput` decodes the reply | A subset schema (closed objects, `type: [..,"null"]`, `anyOf` with null, `enum`, `pattern`, `minLength`, `minItems: 1`, `$schema`, `description`) was accepted and parsed. With `minimum`/`maximum`/`maxItems` added: **400** `property 'maxItems' is not supported`. The SDK's helper `BetaJSONSchemaOutputFormat(map)` **silently returned a nil schema** for that schema (its round trip through `invopop/jsonschema` fails on `type` as an array) → 400 `Input should be an object`; without the array it moves `minLength`, bounds and `$schema` into `description`. A struct pointer works (`invopop/jsonschema` + the same transform) but turns `*string` into a non-nullable `string` | Go S12: don't use the SDK's schema helpers. The `claude` package adjusts the core's schema itself, as .NET's `OutputSchema` does (strip `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `maxItems`, `minItems > 1`), and passes it as `json.RawMessage` |
| 7 | Byte-for-byte replay of raw block JSON | **Works, with a caveat that changes Go S04/G9** | Partly: blocks expose `RawJSON()`; requests take raw JSON only through `param.Override` (or the whole body through `option.WithRequestBody`) | Live: 85 replayed messages, **0 mismatches**, all accepted (thinking with signature, text, tool_use, compaction), with blocks stored *normalized* (below); normalizing changed 14 of 38 blocks, every one a `tool_use` whose `input` keeps the streamed spacing (`{"command": "view", …}`). Offline: `param.Override` sends a raw message through the SDK's fork of `encoding/json` (`internal/encoding/json`, v1 behaviour), which **compacts it and escapes `<` `>` `&`** (`\u003cb\u003e`); `\u00e9` and `\/` are kept. Stored as `json.Compact` + `json.HTMLEscape`, the bytes are a fixed point and go out unchanged. `option.WithRequestBody` sends the bytes as given (the SDK only adds `"stream":true` and still sets `anthropic-beta` from the typed params). Unlike .NET, the accumulated `RawJSON()` is the server's JSON with the deltas set by Go's encoder: non-ASCII stays UTF-8 (`Muñoz`, `«Café»`, `–`), and `+`, `/` in signatures stay literal | Go S04: store each block normalized (compact, HTML-escaped), then `param.Override` per message replays it exactly; or build the body ourselves and send it with `WithRequestBody`. Golden files shared with .NET (`phase-1.md`, across implementations) compare a form both write, not raw block bytes |

### `jsontext.Value` (go.md G9, `encoding/json/v2`)

Checked offline, since G9 now keeps blocks as `jsontext.Value`:

| Encoding of a message holding `{"type":"text", "text":"Caf\u00e9 «Muñoz» <b>&amp;</b> a\/b +x"}` | Output | Block unchanged? |
|---|---|---|
| `jsonv2.Marshal`, default options | `{"type":"text","text":"Café «Muñoz» <b>&amp;</b> a/b +x"}`: whitespace dropped, `\u00e9` and `\/` unescaped | No |
| `jsonv2.Marshal` with `jsontext.PreserveRawStrings(true)` | `{"type":"text","text":"Caf\u00e9 «Muñoz» <b>&amp;</b> a\/b +x"}`: only whitespace dropped | Yes, once the value is compacted (`Value.Compact()` keeps escapes) |
| `encoding/json` v1 `Marshal` (the behaviour of the SDK's fork) | `{"type":"text","text":"Caf\u00e9 «Muñoz» \u003cb\u003e\u0026amp;\u003c/b\u003e a\/b +x"}`: compact, `<` `>` `&` escaped | Only if stored HTML-escaped |
| `param.Override(jsontext.Value)` of the canonical message: block compact and HTML-escaped, message written by v2 with `PreserveRawStrings(true)` | The same bytes on the wire | Yes: message and block byte-identical |

So "replayed unchanged" holds only with one storage form and fixed encoder options. Recommendation for Go S03/Go S04 (step C
of `offline` runs it end to end): the `claude` adapter turns each response block into a `jsontext.Value` in one
canonical form: compact, `<` `>` `&` escaped (`json.HTMLEscape`), other escapes as received. The core writes
conversation JSON with `jsontext.PreserveRawStrings(true)`, and the adapter replays each message through
`param.Override`. That form is a fixed point of the SDK's v1 encoder and of v2 with that option. .NET's stored form
(compact, non-ASCII escaped; its default encoder also escapes `<` `>` `&`, not checked live) should be one too, so a
.NET session should resume byte-exact in Go; Go S08's cross-implementation test confirms it. Go S03 pins the form with a
test: marshal, unmarshal and marshal again a conversation holding such blocks, and compare bytes. The typed parts of a
request (tools, system, settings) are written by each SDK in its own key order, so golden tests compare those as
parsed JSON, not bytes ([`phase-1.md`](../../../docs/plan/phase-1.md), across implementations, S04).

## Against the .NET results

| Item | .NET (S02) | Go (Go S02) |
|---|---|---|
| Features working | 7 of 7 | 7 of 7, same caveats, same numbers within a few tokens |
| Typed access | Typed, raw JSON via `.Json` and `FromRawUnchecked` | Typed for all seven; raw via `RawJSON()` and `param.Override` / `WithRequestBody` |
| Stored block form | SDK re-serialization: non-ASCII and `+` escaped as `\u` | Server JSON with Go-encoded deltas: UTF-8 kept, tool `input` spacing kept; `<` `>` `&` raw until normalized |
| Replay without care | Byte-identical (0 mismatches) | Not byte-identical for blocks with spaces in `input` or with `<` `>` `&`: the SDK's v1 encoder rewrites them; normalize first |
| `usage.iterations`, `context_management` | Raw JSON only | Typed (`Usage.Iterations`, `ContextManagement.AppliedEdits`) |
| Schema from a type | `JsonSchemaExporter`, output rejected until adjusted | SDK helper silently drops a schema with a `type` array; adjust in the `claude` package, as .NET does |
| Streaming | `IAsyncEnumerable` of events, `Aggregate()` | `ssestream.Stream` (`Next`/`Current`/`Err`), `BetaMessage.Accumulate` per event; unknown block types round-trip through `ToParam` as raw |

## Recommendations

**Go S04 (Claude adapter)**
1. Build requests from the typed `BetaMessageNewParams` and send messages with `param.Override`; store every
   response block in the canonical form above. Cover it with a golden test that includes `<`, `&`, non-ASCII and a
   spaced tool `input`.
2. Read tool input from the content-block union's `Input` (`json.RawMessage`), not the typed `BetaToolUseBlock`.
3. Cost a call by summing `Usage.Iterations` when present.

**Go S09 / Go S10:** as S09 and S10; every field needed is typed. **Go S12:** schema adjustment in the `claude` package, no SDK
schema helper.

**No raw-field decisions are needed:** the SDK exposes every feature checked. On-demand compaction
(`compact-2026-09-04`) and the `clear_at` / per-message `output_config` system messages are typed in the SDK but
**unproven**: this spike did not run them, so Go S10 and Go S04 check them live before relying on them.

## Go S10 follow-up: on-demand compaction and `clear_at`, live

**Date:** 2026-10-09 · same SDK and model · **API cost:** about $0.01 (small prompts; on-demand compaction has no
minimum). Run from a throwaway program, not kept.

| Feature | Status | Evidence | Consequence |
|---|---|---|---|
| On-demand compaction (top-level `compaction: {"type":"summarize"}`, beta `compact-2026-09-04`) | **Works, but only by dropping messages** | A request over a 3-message conversation (151 tokens) answered `stop_reason: "compaction"` with one `compaction` block (summary plus a `signature`) and `usage.iterations` = one `compaction` iteration. Sent first, in place of the messages it summarized (as `assistant` or `user`), then a question: answered from the summary. Appended after those messages instead: **400** `compaction_block_misplaced`, "`compaction` block must be sent first, in place of the messages it summarizes; remove those messages" | Not used (D12): it needs the client to drop messages, which principle 5 and HIST-03 forbid. Go S10 uses threshold compaction (`compact_20260112`), whose block is appended |
| `clear_at: "next_user_message"` on a mid-conversation system message (beta `mid-conversation-system-clear-at-2026-08-21`) | **Works** | A run context sent with it was used for the reply it preceded; on the next user turn, with the system message sent unchanged, the model no longer had it ("UNKNOWN"), and the input shrank accordingly. A first wording ("Greet her by name.") was answered with a `refusal` whose details were empty; a neutral one was not | Not used: the run context must stay in view for the whole session, as it is sent only at the start and on a new day (APP-13) |
