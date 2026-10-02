# S12 — Model gateway

**Milestone:** M3 · **Size:** S · **Depends on:** S11 · **Issue:** [#14](https://github.com/sleepyshark85/Officina/issues/14) · **Status:** todo

## Goal

Sharing models safely between many agents.

**Closes:** MDL-04, MDL-05, MDL-08, CLD-10, REL-01, TEST-16

## Acceptance criteria

- [ ] A fallback is used when the primary model is unavailable, and the switch is recorded.
- [ ] All agents share one rate-limit budget per account, served round-robin with the lead first.
- [ ] Retries back off progressively, respect the provider's retry-after, and stop after the configured attempts.

## Notes

From S11:
- MDL-05: the Claude provider classifies failures (`ModelCallException`) with the SDK's own retries off; S12 retries
  the transient and rate-limited ones per configuration, including errors that arrive mid-stream.
- Capabilities per model rather than per provider: some Claude models (Haiku 4.5, Sonnet 5) lack mid-conversation
  system messages, and a fallback must support what its slot uses (MDL-04).
- The fallbacks-used metric (OBS-02, from S08).
