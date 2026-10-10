# Spike Ruby S02: Claude features live check on the Ruby SDK

**Date:** 2026-10-10 · **SDK:** the `anthropic` gem 1.78.0 (latest), Ruby 4.0.7, `json` 2.18.0 · **Model:**
`claude-opus-5-5`, beta surface only (`client.beta.messages`), streamed · **Code:**
[`spikes/ruby-claude-features/claude_features.rb`](../../../spikes/ruby-claude-features/claude_features.rb) · **Output:**
[`spikes/ruby-claude-features/run-output.txt`](../../../spikes/ruby-claude-features/run-output.txt) · **API cost:** $0.49
in all, of which $0.27 was a first compaction call whose run then stopped (below); one clean pass costs about $0.45
($0.47 in Go)

Reruns the .NET spike S02 ([`docs/spikes/claude-features.md`](../../../docs/spikes/claude-features.md)) and Go S02
([`go/docs/spikes/claude-features.md`](../../../go/docs/spikes/claude-features.md)) on the Ruby SDK, with the same
prompts, catalog and thresholds, so the numbers compare. The spike has its own `Gemfile`, outside `ruby/` and any build.
How it holds the conversation: each message is a frozen string of raw JSON, its blocks in the canonical form (compact,
`<` `>` `&` escaped). Everything else in the request is a typed parameter. The messages go out as `JSON::Fragment`s
through **one raw request field**, `request_options: {extra_body: {messages: [...]}}`, which the gem deep-merges after
its typed dump (an Array replaces the typed `messages: []`); its encoder, `JSON.generate`, writes a fragment verbatim. A
client middleware records each request body, every call checks that each stored message appears in it byte for byte,
and a `count_tokens` call (free) before each call stops the run if its worst case could take the spend over $1. An
`offline` step does the same against a local `TCPServer` with bytes chosen to break a re-encoder (no API cost).

**Verdict:** all seven features work on the Ruby SDK, and **every one is typed**: each has a model class, an enum or a
beta constant, and the response fields (`usage.iterations`, `context_management.applied_edits`) are typed. Results match
.NET and Go call for call. Two caveats, both for Ruby S04 and R9: the gem keeps **no raw block JSON** (a block is
written back from its typed model, in the model's field order), and its typed `messages` parameter **re-encodes** what
it is given, so stored bytes are replayed unchanged only through the one raw field above. One caveat for Ruby S04 and
S10: a nilable model field set to `nil` through a setter or `new` raises on read, which the stream helper does to a
compaction block's `encrypted_content`.

## Findings

| # | Feature | Status | Typed in the Ruby SDK? | Evidence | Consequence |
|---|---|---|---|---|---|
| 1 | Server-side compaction (`compact_20260112`, beta `compact-2026-01-12`) | **Works, with the .NET caveats and one of its own** | Yes: `context_management: {edits: [{type: :compact_20260112, trigger: {type: :input_tokens, value:}}]}` (`BetaContextManagementConfig`, `BetaCompact20260112Edit`, `BetaInputTokensTrigger`), beta `AnthropicBeta::COMPACT_2026_01_12`; block `BetaCompactionBlock`; the stream helper handles `compaction_delta` and yields a `CompactionEvent`. `usage.iterations` typed (`BetaCompactionIterationUsage`, `BetaMessageIterationUsage`). The newer `compaction:` parameter (beta `compact-2026-09-04`) is typed too, not tested | Trigger 50,000 on a 52k prompt: `[compaction(1190ch), thinking, text]`, `end_turn`; `iterations` = `{compaction in 48, cache_read 2615, cache_write 50090, out 496} {message in 2, cache_read 2615, cache_write 559, out 97}`; top-level usage is the message iteration only; `applied_edits` `[]`. Stream: `content_block_start` `{"content":null,"type":"compaction"}`, one `compaction_delta`. **Then `block.encrypted_content` raised** `ConversionError` (NilClass to String), stopping run 2: the stream helper copies the delta's `encrypted_content` (absent, so `nil`) through the setter, and in this gem version a nilable field set to `nil` through a setter or `new` raises on read (`BetaTextBlock.new(text: "x", citations: nil).citations` too), while a parsed `null` or a missing key reads `nil`. `block[:encrypted_content]` reads `nil`. Run 3 (raw reader): calls 2 and 3 replayed the block (stored with `"encrypted_content":null`, accepted), read 2615 and 3137 from cache; answers right (shelf Q2, 17.42 EUR) | Ruby S10 as S10: cost and report compaction from `usage.iterations`. Read streamed compaction blocks raw (`block[:content]`, `block[:encrypted_content]`), not through their typed readers. Ruby S04: the Claude gem reads nilable block fields with `block[:field]`, and never builds models with `nil` through setters or `new` |
| 2 | Tool-result clearing (`clear_tool_uses_20250919`, beta `context-management-2025-06-27`) | **Works, with the .NET caveat** | Yes: `{type: :clear_tool_uses_20250919, trigger: {type: :tool_uses, value: 2}, keep: {type: :tool_uses, value: 1}}`; response `context_management.applied_edits[i].cleared_input_tokens` / `cleared_tool_uses` (`BetaClearToolUses20250919EditResponse`), set from `message_delta` by the stream helper | First applied on call 4 (`4892` tokens, `2` uses), then call 5 (`4799`, `2`), as in .NET and Go. Call 4's cache read fell back to the system prompt (3035) | Ruby S10: report the typed `applied_edits`; set `clear_at_least` outside demo mode |
| 3 | Memory tool (`memory_20250818`) | **Works** | Yes: `Anthropic::Beta::BetaMemoryTool20250818.new` → `{"name":"memory","type":"memory_20250818"}`, no beta header; commands typed (`BetaMemoryTool20250818ViewCommand`…), not needed | A: `view /memories` → `create /memories/customers/ana.md`. B (fresh): `view` → `view` file → answer used the note ("If you're Ana…"). Prefix cache write 4269 (vs 2671 without the tool) | Ruby S09 as S09. A tool block's `input` is a Hash with Symbol keys, decoded by the stream helper at `content_block_stop` |
| 4 | Thinking `display: "updates"` (beta `thinking-display-updates-2026-08-18`) in a tool loop | **Works, with the .NET caveat** | Yes: `thinking: {type: :adaptive, display: :updates}` (`BetaThinkingConfigAdaptive::Display::UPDATES`); deltas as `BetaThinkingDelta` / `BetaSignatureDelta`, plus the helper's `ThinkingEvent` / `SignatureEvent` | Thinking blocks only on the first and last turns, each `"thinking":""` from one empty `thinking_delta` and one `signature_delta`. Progress came as `text` blocks before `tool_use` ("I'll start with…"); this run made both lookups of a title in one reply | Ruby S04/Ruby S06: thinking text optional and possibly empty; show text blocks between tool calls as progress |
| 5 | Mid-conversation `system` message for run context, cache point on the last system block plus top-level automatic caching | **Works** | Yes: `BetaMessageParam` role `:system`, `clear_at` and per-message `output_config` typed | `BetaMessageParam.new(role: :system, content: [{type: :text, text:}]).to_json` → `{"role":"system","content":[{"text":"Run context: …","type":"text"}]}` (blocks, model field order; accepted). Model used both facts. Cache: call 1 wrote 2671; call 2 read 2671 / wrote 133; call 3 read 2804 / wrote 179 (warm rerun: 2671; 2671 / 141; 2812 / 160) | Ruby S04 as designed (CTX-02, CTX-03) |
| 6 | Structured output with a schema the core's validator subset produces | **Works, with adjustments** | Yes: `output_config: {format: {type: :json_schema, schema: Hash}}` (`BetaJSONOutputFormat`); or an `Anthropic::BaseModel` subclass as `format:`, which the gem turns into a schema and decodes into `message.parsed_output` | The subset schema (closed objects, `type: [..,"null"]`, `anyOf` with null, `enum`, `pattern`, `minLength`, `minItems: 1`, `$schema`, `description`) was accepted. With `minimum`/`maximum`/`maxItems`: **400** `For 'integer' type, properties maximum, minimum are not supported`. A model class (`required :isbn, String, nil?: true`, `EnumOf`, `ArrayOf`, a nested nilable model) gave an accepted schema (`"type":["string","null"]`, `anyOf` for the nilable object, `enum` with `"type":"string"`) and a typed `parsed_output`. The gem's private `SupportedSchemas.transform_schema!` moves unsupported keywords into `description`, as Go's helper does | Ruby S12: the schema comes from the core's DSL (R19), not the gem's model classes; the Claude gem strips what the API rejects (`minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `maxItems`, `minItems > 1`), as .NET and Go do, and passes a Hash |
| 7 | Byte-for-byte replay of stored block JSON | **Works, with caveats that change Ruby S04 and R9** | Partly: blocks are typed models with no raw JSON; requests take raw JSON only through `request_options: {extra_body:}` (or a whole `StringIO` body from a middleware) | Live: 101 replayed messages, **0 mismatches**, all accepted (thinking with signature, text, tool_use, compaction). Offline: a message `{"type":"text","text":"Caf\u00e9 «Muñoz» <b>&amp;</b> a\/b +x"} , {…}` sent as a `JSON::Fragment` in `extra_body` arrives **unchanged, spacing included**; the middleware's `JSON.generate(req.body)` equals the bytes the server read. The same message parsed and passed as typed `messages:` is **re-encoded**: `\u00e9` → `é`, `\/` → `/`, spacing dropped (`<` `>` `&` stay raw). A `StringIO` body swapped in by a request middleware goes out as is; a `String` body is JSON-encoded as a string. Stored blocks come from `block.to_json`, the gem's dump of its model: Ruby's encoder (UTF-8 kept, `/` raw), **fields in the model's declared (alphabetical) order** (`{"signature":…,"thinking":"","type":"thinking"}`), a field set after the start event last (`{"content":…,"type":"compaction","encrypted_content":null}`); `to_h` has the same order | Ruby S04: write each response block as `canonical(block.to_json)` (escape `<` `>` `&` only) and send stored messages as `JSON::Fragment`s through `extra_body: {messages:}`, never through the typed `messages:`. Any stored bytes then replay exactly, a .NET or Go block included. Golden files shared with .NET and Go compare a form all three write, not raw block bytes: the same reply is stored with different key order in each |

## How blocks are stored and replayed

| Step | What the spike does | Bytes |
|---|---|---|
| Receive | `client.beta.messages.stream(…)`, then `stream.accumulated_message`; each block is a typed model (`BetaTextBlock`, `BetaThinkingBlock`, `BetaToolUseBlock`, `BetaCompactionBlock`) | The server's JSON is parsed and coerced; its key order and escapes are not kept |
| Store | `canonical(block.to_json)`: the gem's dump written by `JSON.generate`, then `<` `>` `&` replaced by `\u003c` `\u003e` `\u0026` (they occur only inside strings, so the substitution is safe); a message is `{"role":"assistant","content":[…]}` around those strings | Compact, UTF-8, `/` unescaped, model field order |
| Replay | `stream(**typed_params, messages: [], request_options: {extra_body: {messages: stored.map { JSON::Fragment.new(it) }}})` | Each stored message byte for byte on the wire (live: 101 of 101; offline: spacing, `\u00e9` and `\/` kept) |

`JSON::Fragment` needs `json` 2.10 or later (Ruby 4.0.7 ships 2.18.0). The typed `messages:` parameter stays `[]` and
the gem's validation of messages is bypassed, so the core's own conversation rules carry that check. The typed parts of
a request (tools, system, settings) are written by the gem in its models' field order, so golden tests compare them as
parsed JSON, as Go's do ([`phase-1.md`](../../../docs/plan/phase-1.md), across implementations, S04).

## Against the .NET and Go results

| Item | .NET (S02) | Go (Go S02) | Ruby (Ruby S02) |
|---|---|---|---|
| Features working | 7 of 7 | 7 of 7 | 7 of 7, same caveats, same numbers within a few tokens |
| Typed access | Typed; `usage.iterations`, `context_management` raw only | Typed for all seven | Typed for all seven; streamed compaction's `encrypted_content` read raw |
| Stored block form | SDK re-serialization: non-ASCII and `+` escaped | Server JSON with Go-encoded deltas, normalized | The gem's model dump: UTF-8, model field order, then `<` `>` `&` escaped |
| Raw replay channel | `FromRawUnchecked` | `param.Override` per message (re-encoded by Go's v1 encoder) | `extra_body: {messages: [JSON::Fragment]}`: verbatim, any bytes |
| Replay without care | Byte-identical | Not byte-identical for spaced `input` or `<` `>` `&`; normalize first | Typed `messages:` re-encodes; through the raw field, byte-identical |
| Schema from a type | Exporter output rejected until adjusted | SDK helper silently drops a schema with a `type` array | `BaseModel` class accepted; the core's DSL is the source anyway |
| Streaming | `IAsyncEnumerable`, `Aggregate()` | `Accumulate` per event | `stream.each` yields raw and helper events; `accumulated_message` |

## Recommendations

**Ruby S04 (Claude gem)**
1. Build requests from typed parameters; send the conversation as `JSON::Fragment`s through
   `request_options: {extra_body: {messages:}}`, the one raw field, confined to the Claude gem. Store every response
   block as `canonical(block.to_json)`. Cover it with a golden test that includes `<`, `&`, non-ASCII, `\u` escapes and
   a spaced tool `input`, checked on a local server.
2. Read tool input from the block's `input` (a Hash with Symbol keys); cost a call by summing `usage.iterations` when
   present.

**Ruby S09 / Ruby S10:** as S09 and S10; read streamed compaction blocks raw. **Ruby S12:** schema from the DSL,
adjusted in the Claude gem.

**Decision change:** R9 gains the replay channel and how blocks are written (this PR). On-demand compaction
(`compact-2026-09-04`) and `clear_at` are typed but unproven here; Go S10 tried both live, and Ruby S10 checks threshold
compaction and clearing live before relying on them.
