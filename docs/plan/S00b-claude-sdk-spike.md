# S00b — Claude SDK spike

**Milestone:** M0 · **Size:** S · **Depends on:** none · **Issue:** [#2](https://github.com/sleepyshark85/Officina/issues/2) · **Status:** done

## Goal

Confirm that the Anthropic C# SDK supports everything the Claude provider needs.

**Closes:** nothing (a spike: findings only)

## Acceptance criteria

- [x] A throwaway console app exercises: streaming with tools; cache markers with 5-minute and 1-hour lifetimes; mid-conversation and turn-scoped system messages; adaptive thinking with effort; reasoning blocks sent back unchanged; structured output; the SDK's option for raw request fields; error types; usage including cache by lifetime.
- [x] `docs/spikes/claude-sdk.md` lists each feature as typed, raw-only or missing, with the workaround.

## Notes

Costs a few cents of API usage and needs `ANTHROPIC_API_KEY`.
