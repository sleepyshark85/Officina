# Spike S00b: Anthropic C# SDK

**Date:** 2026-10-01 · **SDK:** NuGet `Anthropic` 12.53.0 (latest), .NET 10 · **Model:** `claude-opus-5-5`
**Code:** [`spikes/claude-sdk/Program.cs`](../../spikes/claude-sdk/Program.cs) · **Output:** [`spikes/claude-sdk/run-output.txt`](../../spikes/claude-sdk/run-output.txt) · **API cost:** about $0.07

**Verdict:** the SDK covers everything the Claude provider needs. Only two things need the raw-data
option: `additionalProperties: false` in a tool's input schema (needed for strict tools), and
fields that exist only on the beta request type when you call the non-beta endpoint. Nothing is
missing.

## Findings

| # | Feature | Status | Evidence | What the provider does |
|---|---|---|---|---|
| 1 | Streaming with a client tool, eager input streaming | Typed | `Tool.EagerInputStreaming = true` was serialized as `"eager_input_streaming":true`. `CreateStreaming` returned `input_json_delta` events (`{"city": "Madrid"}`). `SseAggregatorExtensions.Aggregate()` rebuilt the final `Message` from the buffered events. | Collect events, aggregate them, and parse and validate the tool input before running the tool. The server does not validate input when eager streaming is on. |
| 2 | `cache_control` 1h on the last system block, 5m on the last message block | Typed | `CacheControlEphemeral { Ttl = Ttl.Ttl1h / Ttl.Ttl5m }` on `TextBlockParam`. Call 1: `cache_creation=5693 (5m=2170, 1h=3523)`. Call 2: `cache_read=5693`, `cache_creation=0`. | Use the typed fields. Also typed: `Tool.CacheControl` and the top-level `CacheControl`. |
| 3 | Mid-conversation `role: "system"` message | Typed, non-beta | `Role.System` exists in both `Anthropic.Models.Messages.Role` and the beta `Role`. A `[user, system]` request was accepted on `client.Messages`, and the model followed it (it replied in French). | Use the typed field. |
| 4 | Turn-scoped system message (`clear_at: "next_user_message"`) | Typed, beta only | `Beta.BetaMessageParam.ClearAt = ClearAt.NextUserMessage` with `Betas = ["mid-conversation-system-clear-at-2026-08-21"]`. Turn 1 answered "PELICAN". In turn 2 the message was still in the array, but the model said no system message was visible. | Use `client.Beta.Messages`. The non-beta `MessageParam` has no `ClearAt`. |
| 5 | Adaptive thinking + `output_config.effort`; thinking blocks sent back unchanged | Typed | `ThinkingConfigAdaptive { Display = Display.Summarized }` and `OutputConfig { Effort = Effort.High }`. The stream produced `thinking_delta` and `signature_delta` events, and the response held `[ThinkingBlock, TextBlock, ToolUseBlock]` (signature 816 chars). The block was sent back as `ThinkingBlockParam { Thinking, Signature }` with the `tool_result`, and the API accepted it (`end_turn`). | There is no `.ToParam()`, so convert each block type by hand. Keep `Signature` exactly as received, and store the raw JSON of each block (`block.Json`) so history replays byte for byte. |
| 6a | Structured output (`output_config.format`) | Typed | `OutputConfig.Format = new JsonOutputFormat { Schema = ... }` returned `{"capital":"Paris","population_millions":68.2}`. | Use the typed field. The schema is a raw `Dictionary<string, JsonElement>` by design. |
| 6b | `strict: true` tools | Typed, but schema needs raw | `Tool.Strict = true` is typed. `InputSchema` types only `type`, `properties` and `required`, so `additionalProperties: false` needs `new InputSchema(IReadOnlyDictionary<string, JsonElement>)`. Sent and accepted. | Always build `InputSchema` from the core's JSON schema through the raw-dictionary constructor. That passes the schema through unchanged, which is what we want anyway. |
| 7 | Sending fields that have no typed property | Raw option works | Every params/model type stores its JSON in `RawBodyData` / `RawData`. `MessageCreateParams.FromRawUnchecked(headers, query, body)` (or the model's `new X(IReadOnlyDictionary<string, JsonElement>)`) sends any extra key. Proof: (a) `fallbacks: "default"` plus a raw `anthropic-beta` header on the non-beta params returned HTTP 200; (b) `not_a_real_param: 1` reached the wire and returned 400 `not_a_real_param: Extra inputs are not permitted`. Response extras are readable through `message.RawData`. | Use this only for fields not yet typed. Note that the raw-data constructor does not satisfy C# `required` members (CS9035), so use `FromRawUnchecked`. |
| 8 | Error types | Typed | 400: `AnthropicBadRequestException : Anthropic4xxException : AnthropicApiException`, with `StatusCode=400`, `ErrorType=InvalidRequestError` and `ResponseBody` (JSON). 404: `AnthropicNotFoundException`. A streaming 400 is thrown at HTTP level as the same `AnthropicBadRequestException`. A **mid-stream** overload actually happened during this spike: `AnthropicSseException` with `ErrorType=OverloadedError`. By the docs and source, 429 is `AnthropicRateLimitException`, and 529 (and every 5xx) is `Anthropic5xxException`. `ErrorType` covers `invalid_request, authentication, permission, not_found, rate_limit, timeout, overloaded, api, billing`. Network errors are `AnthropicIOException`. | Classify on exception type first, then on `ErrorType` (needed for SSE errors, which carry no status). "Input too long" needs the message text, because it is a plain `InvalidRequestError`. |
| 9 | Usage, including cache creation by TTL | Typed | `Usage.InputTokens`, `OutputTokens`, `CacheReadInputTokens`, `CacheCreationInputTokens`, `CacheCreation.Ephemeral5mInputTokens` / `Ephemeral1hInputTokens`, `ServiceTier`, `ServerToolUse`, `InferenceGeo`. `BetaUsage` adds `Iterations`, `Speed` and `FallbackCredit`. | Use the typed fields. |
| — | Stop reasons | Typed, open | `StopReason` is `ApiEnum<string, StopReason>` with `EndTurn, MaxTokens, StopSequence, ToolUse, PauseTurn, Refusal, ModelContextWindowExceeded`. `.Raw()` keeps unknown values. | Map on `.Raw()` strings so an unknown value becomes "unknown" (CLD-04). |

## Surprises

- **SDK retries do not cover mid-stream errors.** The first streaming call failed twice in a row
  with an SSE `overloaded_error` after HTTP 200. The SDK's automatic retries (2 by default, for
  408/409/429/5xx and connection errors) apply only before the response starts. A plain stream
  sent at the same moment succeeded, so the overload was transient. The provider must treat
  `AnthropicSseException` with `OverloadedError` / `ApiError` as transient and retry the whole call itself.
- **Beta and non-beta are two separate type hierarchies.** `Anthropic.Models.Messages.*` and
  `Anthropic.Models.Beta.Messages.*` share no types. For example, `Effort` is
  `Anthropic.Models.Beta.Messages.Effort` on the beta path, and both namespaces define `Role`. Turn-scoped
  messages, `Fallbacks`, `ContextManagement` (history shortening, clearing tool results), `TaskBudget`,
  `Speed` and `Diagnostics` are typed only on the beta params.
- `MessageCreateParams` raw constructor lacks `[SetsRequiredMembers]` → use `FromRawUnchecked`.
- Structured output adds about 200 input tokens (234 for a tiny prompt), because the schema is rendered into the prompt.
- At `effort: low` the model skipped thinking on a simple tool call (no thinking block). The
  round-trip test needed `effort: high` and a small puzzle.
- The minimum cacheable prefix on Opus 5.5 is 512 tokens. The 1h/5m split appears in `usage.cache_creation` as expected.

## Recommendations for S11 (Claude provider)

1. Build the provider entirely on `client.Beta.Messages` and the `Anthropic.Models.Beta.Messages`
   types. The beta types are a superset of the non-beta ones and include every CLD-06 switch. Send `Betas` per enabled
   feature flag. Don't mix in the non-beta types (they collide on `Role` and `Effort`).
2. Translate core → SDK through the raw-dictionary constructors where the core already holds JSON
   (tool input schemas, structured-output schemas, stored reasoning blocks). Use typed setters elsewhere.
   CLD-02 offline tests can serialize `RawBodyData` and compare it to golden JSON without a network
   connection. This spike already did that (`JsonSerializer.Serialize(p.RawBodyData[...])`).
3. Stream with `CreateStreaming` and build the final message with `.Aggregate()`. Validate tool input
   against its schema before running the tool.
4. Store assistant content blocks as raw JSON (`ContentBlock.Json`) and rebuild the params from it,
   so thinking blocks and signatures replay exactly (CLD-05, CTX-10). The per-variant hand conversion
   is easy to get wrong when the SDK adds new block types.
5. Error mapping (CLD-08): `AnthropicRateLimitException` → rate-limited. `Anthropic5xxException`,
   `AnthropicIOException`, and `AnthropicSseException` with `ErrorType` overloaded/api/timeout → transient.
   `AnthropicUnauthorizedException`/`AnthropicForbiddenException` → authentication.
   `AnthropicBadRequestException` whose message reports an over-long prompt → input too long.
   Other 4xx → invalid request. Turn off the SDK's own retries (`MaxRetries = 0`) so the core's rate-limit view (CLD-10) is the
   only retry policy, or keep them and accept that they are invisible to the core.
6. Don't put `cache_control` on a `clear_at` system message (the docs say this is a 400). Boundary ③ goes on the
   block before it, which is what DESIGN §3 already does.
