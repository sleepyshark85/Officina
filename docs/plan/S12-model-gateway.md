# S12 — Model gateway

**Milestone:** M3 · **Size:** S · **Depends on:** S11 · **Issue:** [#14](https://github.com/sleepyshark85/Officina/issues/14) · **Status:** done

## Goal

Sharing models safely between many agents.

**Closes:** MDL-04, MDL-05, MDL-08, CLD-10, REL-01, TEST-16

## Acceptance criteria

- [x] A fallback is used when the primary model is unavailable, and the switch is recorded.
- [x] All agents share one rate-limit budget per account, served round-robin with the lead first.
- [x] Retries back off progressively, respect the provider's retry-after, and stop after the configured attempts.

## Notes

Built:
- `ModelGateway` (Core) sits between every turn and its provider. Per provider it holds one line for all agents:
  `maxConcurrentCalls` calls in flight (unset: no cap), the rest waiting in the order they came, the team lead's first
  (MDL-08, CLD-10). Teams are found by the `team` pattern's `lead`.
- Retries (REL-01, MDL-05): `providers.<name>.retry` (`maxAttempts` counting the first call, `initialDelay` doubling up to
  `maxDelay`). A `Retry-After` from the provider (`ModelCallException.RetryAfter`) lengthens the wait and holds back every
  agent of that provider. Invalid-request and authentication failures are not retried. A failure mid-stream retries the
  whole call; the gateway sends `ReplyRestarted` and the turn drops what it had of the reply.
- Fallbacks (MDL-04): `models.<name>.fallbacks`, tried in order after the profile's attempts; each has its own. The next call
  tries the primary again. The gateway sends `FallbackUsed`; the turn publishes `modelFallback`, counts `officina.fallbacks`
  (OBS-02) and prices the rest of the call by the fallback's model. Validation checks that each fallback exists and that its
  model runs the provider tools of the slot and, if the slot's model has them, takes turn-scoped messages.
- `IModelProvider.CapabilitiesOf(model)` replaces `Capabilities`. The Claude provider leaves out turn-scoped messages for
  `claude-haiku-4-5*` and `claude-sonnet-5*`, so they get the append-only form. Its HTTP transport notes `Retry-After`,
  because the SDK's exceptions carry no headers. `ClaudeProvider` and `HttpRecording` now take an `HttpMessageHandler`.

Left to S21: see the follow-ups in the plan.

From S11:
- MDL-05: the Claude provider classifies failures (`ModelCallException`) with the SDK's own retries off; S12 retries
  the transient and rate-limited ones per configuration, including errors that arrive mid-stream.
- Capabilities per model rather than per provider: some Claude models (Haiku 4.5, Sonnet 5) lack mid-conversation
  system messages, and a fallback must support what its slot uses (MDL-04).
- The fallbacks-used metric (OBS-02, from S08).
