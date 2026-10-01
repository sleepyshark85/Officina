# S11 — Claude provider

**Milestone:** M3 · **Size:** M · **Depends on:** S00b, S05 · **Issue:** [#13](https://github.com/sleepyshark85/Officina/issues/13) · **Status:** done

## Goal

The real model, through the Anthropic C# SDK.

**Closes:** MDL-06, CLD-01, CLD-02, CLD-03, CLD-04, CLD-05, CLD-07, CLD-08, CLD-09, CLD-12, TEST-02

## Acceptance criteria

- [x] Request and response mapping is tested offline against golden JSON, including reasoning blocks sent back unchanged.
- [x] Cache markers are sent at each boundary with the right lifetimes, and the volatile context uses the turn-scoped form when available.
- [x] Every stop reason and error maps as in DESIGN.md §9; anything unrecognised is reported as unknown.
- [x] Usage is priced from the shipped table, including cache writes by lifetime.
- [x] A live recording replays offline exactly.
- [x] An opt-in live test passes and shows cache reads on the second call of a turn.

## Notes

- `ClaudeProvider` uses the SDK's beta API and streams every call. Golden JSON tests replay recorded HTTP exchanges
  (`HttpRecording` in the test kit), so the provider and the SDK run for real. The live test records with
  `OFFICINA_RECORD_LIVE=1` and `ANTHROPIC_API_KEY`; its recording is replayed offline in every build.
- A prompt the API reports too long is the stop reason `InputTooLong`, so the turn shortens the history (HIST-04).
  Other failures are a `ModelCallException` with the MDL-05 category, named in the handoff.
- The price table ships as the `claude` provider's default `prices`, as of 2026-10. Every model profile needs a price,
  because cost budgets always exist (INV-07).
- Profile `settings` are sent as top-level request fields, as JSON where they parse (CLD-01), for example `thinking`.
- A host shares one `KnownSecrets` with the providers, the tool servers and the runner, so the secrets they read are
  removed from what tools return (INV-06). S16 does this in `sof run`.
- Text blocks are sent back without their citations, and consecutive text blocks as one.

Moved to the slices that add what they need:
- MDL-05 to S12: S11 classifies failures; S12 retries transient and rate-limited ones (REL-01). The SDK's own retries
  are off.
- CLD-06 to S21: native structured output (`output_config.format`, the S06 follow-up), compaction as the provider's
  `IHistoryShortener` (the S07 follow-up), clearing old tool results, task budgets and the refusal fallback, each a
  provider feature switch. Until then `provider` shortening is not available with Claude.
- CLD-11 to S21: `ModelRequest.Batch` to Message Batches (MDL-10, the S09 follow-up). Until then batch work runs as
  ordinary calls.
- S12: capabilities per model rather than per provider, such as mid-conversation system messages, which some models
  (Haiku 4.5, Sonnet 5) lack; fallbacks need the same check (MDL-04). The fallbacks-used metric (OBS-02).
