# Spike S02: Claude features live check

**Date:** 2026-10-05 · **SDK:** NuGet `Anthropic` 12.53.0 (latest), .NET 10 · **Model:** `claude-opus-5-5`, beta types only
(`client.Beta.Messages`), streamed · **Code:** [`spikes/claude-features/Program.cs`](../../spikes/claude-features/Program.cs)
· **Output:** [`spikes/claude-features/run-output.txt`](../../spikes/claude-features/run-output.txt) · **API cost:** about $0.53
(compaction alone $0.27)

Continues agentic-core spike S00b (`~/sources/agentic-core/docs/spikes/claude-sdk.md`). The spike is not part of the
solution. How it holds the conversation: messages are raw JSON, and each assistant block is stored as `block.Json.GetRawText()`.
The request is rebuilt with `MessageCreateParams.FromRawUnchecked`. An HTTP handler captures each request body so the
replay can be checked byte for byte.

**Verdict:** all seven features work on Opus 5.5 with the beta types. Five have caveats; these change phase 1 details:
the compaction trigger minimum, how compaction is reported, the structured-output schema subset, and progress notes.

## Findings

| # | Feature | Status | Evidence | Consequence |
|---|---|---|---|---|
| 1 | Server-side compaction (`compact_20260112`, beta `compact-2026-01-12`) | **Works, with caveats** | Trigger `{"type":"input_tokens","value":50000}`, which is the **minimum** per the platform docs (not probed below 50k). A 52.7k-token prompt returned `[compaction(1156 chars), thinking, text]` with `stop=end_turn`. Streaming: `content_block_start` with `"content":null`, then a single `compaction_delta` holding the whole summary. `usage.iterations` = `[{type:compaction, input 48 + cache_read 2615 + cache_write 50090, output 578}, {type:message, in 2, cache_read 2615, cache_write 558, out 85}]`. Top-level usage shows the message iteration only. `context_management.applied_edits` stayed `[]`. Call 2 sent the response appended as received: it was accepted with 4 input + 2615 cache-read tokens, and the thinking block that followed the compaction block replayed without error. Call 3 read 3280 tokens from cache (system plus compaction block). Answers after compaction were correct (shelf Q2, 17.42 EUR). | S10: cost and report compaction from `usage.iterations`, not from `applied_edits`. "Tokens removed" is the compaction iteration's input. The demo threshold cannot go below 50k (per the platform docs, not probed below 50k). |
| 2 | Tool-result clearing (`clear_tool_uses_20250919`, beta `context-management-2025-06-27`) | **Works, with caveat** | Trigger `{"type":"tool_uses","value":2}`, keep `1`. Clearing first applied on call 4, with 3 tool uses in history: `applied_edits=[{cleared_input_tokens:4892, cleared_tool_uses:2}]`. On call 5: `4799, 2`. Cache reads: calls 2–3 read the growing prefix (3035 → 5709). On call 4 the read **fell back to the system prompt (3035)** and 2808 tokens were written. Call 5 read 5843, which is the cleared prefix call 4 had written. The model noticed: "I can only confirm the author for two of them". Thinking blocks replayed with no 400 (server-side clearing is not an edit). | S10: report `cleared_input_tokens` / `cleared_tool_uses` per call. Every new clear costs a cache rewrite of the tail, so set `clear_at_least` outside demo mode. |
| 3 | Memory tool (`memory_20250818`) on the beta path | **Works** | `Tools=[new BetaMemoryTool20250818()]` serializes to `{"name":"memory","type":"memory_20250818"}`. **No beta header needed.** Conversation A: `view /memories` → `create /memories/customers/ana.md`. Conversation B (fresh): `view /memories` → `view /memories/customers/ana.md` → answer used the note. The tool adds about **1.6k tokens** to the cached prefix (cache write 4269 vs 2671 without it). | S09: implement view/create/str_replace/insert/delete/rename against the scoped store. The model invents its own paths under `/memories`. In B it did not know the user ("If you're Ana…"), so the run context must name the user (MEM-03). |
| 4 | Thinking `display: "updates"` (beta `thinking-display-updates-2026-08-18`) in a tool loop | **Works, with caveat** | `{"type":"adaptive","display":"updates"}` was accepted. Three runs were made (a first, shorter one was overwritten in the output file, as its header notes; the two shown are medium effort with "keep me posted" and high with a plain 3-title task). All gave **no progress-note text in thinking blocks**. Each thinking block streamed one empty `thinking_delta` plus one `signature_delta`, and arrived as `"thinking":""` with a signature of about 1k chars. They appeared only on the first and last turns of a loop. Progress narration came as ordinary `text` blocks before the `tool_use` blocks ("I'll start with the first title."). All replayed blocks were accepted (21 and 27 in the two runs shown). | S04/S06: treat thinking text as optional and possibly empty. Render text blocks between tool calls as progress. Don't build UI that depends on update notes. |
| 5 | Mid-conversation `system` message for run context, with `cache_control` on the last top-level system block plus top-level automatic caching | **Works** | Wire: `[{"role":"user",…},{"role":"system","content":"Run context: today is 2026-10-05; the customer is Ana Muñoz…"}]`. The model used both facts. First run: call 1 wrote 2671; call 2 read 2671 / wrote 212; call 3 read 2883 / wrote 139. Second run (in the output file, cache still warm): call 1 read 2671; call 2 read 2671 / wrote 137; call 3 read 2808 / wrote 177. The automatic breakpoint moves to the tail, including past the trailing system message. | S04 as designed (CTX-02, CTX-03). |
| 6 | Structured output with a `JsonSchemaExporter` schema | **Works, with adjustments** | The exporter's output was **rejected**: `For 'object' type, 'additionalProperties' must be explicitly set to false`. With that added to every object (nested and nullable ones included) it was accepted, and the reply deserialized into the record. Keyword probes, details below. | S12: post-process the exported schema (OUT-01): add `additionalProperties:false`, drop unsupported keywords and don't use `JsonSerializerOptions.Web` for export. |
| 7 | Byte-for-byte replay of raw block JSON | **Works, with caveat** | 0 mismatches over every replayed assistant block (thinking with signature, text, tool_use, compaction) across all runs. Each stored `GetRawText()` appears unchanged in the captured request body, and the server accepted every replay. However, the stored `.Json` is the **SDK's re-serialization** of the aggregated stream, not the server's bytes. It escapes non-ASCII and `+` (`Muñoz`, `«Café`, `+` inside signatures). | S04: store `block.Json.GetRawText()` and rebuild from it. It is stable from store to wire, which is what CLD-05 / MDL-05 need. Golden tests compare that form, not the SSE bytes. |

### Structured-output keyword probes (one property `x`, object closed)

| Keyword | Result |
|---|---|
| `type: [..., "null"]`, `anyOf` with null, `enum`, `$ref` + `$defs`, `$schema`, `default`, `format: "date"`, `minLength`/`maxLength`, `minItems: 1` | Accepted |
| `pattern` | Accepted and **enforced**: with `^[0-9]{3}$`, "set x to hello" gave `"000"` |
| `minimum` / `maximum` (integer or number) | **400** `properties maximum, minimum are not supported` |
| `maxItems` | **400** `property 'maxItems' is not supported` |
| `additionalProperties` missing on an object | **400** |
| `type: ["string","integer"]` + `pattern` (the exporter's output for numbers with `JsonSerializerOptions.Web`) | Accepted, but it lets the model answer a string; export with options that don't read numbers from strings |

## Surprises

- **Compaction's minimum trigger is 50,000 tokens**, and a compaction call costs about $0.25 on Opus 5.5. With automatic
  caching on, the compaction iteration also **wrote the whole 50k pre-compaction prompt to cache** at 1.25×, which is
  wasted because it is never read again.
- Compaction does **not** appear in `context_management.applied_edits`. Only the block and `usage.iterations` show it.
- `trigger.tool_uses = 2` fired once history held **3** tool uses ("more than"). With `keep 1`, it cleared 2 both times,
  not "all but one".
- The memory tool works on `client.Beta.Messages` with no beta flag, and the model checks `/memories` unprompted at
  the start of each conversation, even at medium effort.
- Thinking is skipped on middle turns of a tool loop at every effort tried, high included. Thinking blocks appear on
  the first and final turns.
- The docs now recommend a newer **on-demand compaction** (`compact-2026-09-04`, top-level `compaction` parameter) over
  threshold compaction. With it, the client chooses when to compact and can keep recent turns with their thinking. It
  was not tested here.

## Recommendations

**S04 (Claude adapter)**
1. Hold assistant content as `block.Json.GetRawText()` and rebuild requests with `FromRawUnchecked` plus a raw
   `messages` array. This spike shows the approach works across every block type seen.
2. Cost a call by summing `usage.iterations` when present. Top-level usage leaves out the compaction iteration.
3. Run context as a trailing `system` message, cache point on the last instructions block, top-level automatic caching:
   confirmed, keep as designed.
4. Treat thinking text as possibly empty under every `display`, and show `text` blocks between tool calls as progress.

**S09 (Memory)**
5. Map the six memory commands onto the scoped store, and return errors as tool results. Count about 1.6k extra prefix
   tokens when memory is enabled. Make sure the run context names the user, because memory paths alone don't.

**S10 (Long conversations)**
6. Compaction event: tokens = compaction iteration input (input + cache read + cache write), summary size = its output.
   Clearing event: `cleared_input_tokens`, `cleared_tool_uses` from `applied_edits`.
7. Demo mode (APP-17): compaction can't trigger below 50k tokens, so a "short session" needs a large tool result or
   seeded context to reach it. Each demo compaction costs about $0.25. Tool-result clearing can trigger after a few
   calls (`tool_uses` trigger).
8. Consider leaving automatic caching off on the request that is expected to compact, or accept the extra write. Decide
   whether on-demand compaction (`compact-2026-09-04`) should replace the threshold edit before building S10.

**S12 (Typed output)**
9. Schema adjustment for OUT-01: export with default options (not `Web`) and a string enum converter, set
   `additionalProperties:false` on every object, and strip `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`,
   `multipleOf`, `maxItems` and `minItems > 1`. Probed: `minimum`/`maximum`, `maxItems` (rejected), `minItems: 1` (accepted). Per docs, not probed: `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `minItems > 1`. Check those keywords client-side after deserializing, or not at all
   (OUT-02). `pattern` can stay.

## Against REQUIREMENTS and ARCHITECTURE

| Item | Finding |
|---|---|
| APP-17 "lowers the compaction threshold so it is visible in a short session" | Only partly possible. The floor is 50k input tokens, so the demo needs big seeded content. |
| HIST-04 "reported with the tokens they removed" | Possible, but compaction's numbers come from `usage.iterations`, not from the context-management report clearing uses. |
| OUT-01 "adjusted to the subset the provider accepts" | Confirmed necessary: the raw exporter output is rejected. |
| ARCHITECTURE §10 "display chosen per agent (progress updates for interactive agents)" | `updates` is accepted, but no progress notes were seen. Progress arrived as text blocks. Keep the setting, but don't depend on it. |
| MDL-05 "as received" | Disagrees as worded. "As received" means the adapter's parsed block JSON (the SDK's re-serialization, with non-ASCII and `+` escaped as `\u`), not the server's bytes. That JSON stays byte-identical from store to wire, which is what replay and S04's golden tests rely on. |
| D5, CTX-02, CTX-03, MEM-01, MEM-05, HIST-01, HIST-02 | Confirmed as written. |
