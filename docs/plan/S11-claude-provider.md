# S11 — Claude provider

**Milestone:** M3 · **Size:** M · **Depends on:** S00b, S05 · **Issue:** [#13](https://github.com/sleepyshark85/Officina/issues/13) · **Status:** todo

## Goal

The real model, through the Anthropic C# SDK.

**Closes:** MDL-05, MDL-06, CLD-01, CLD-02, CLD-03, CLD-04, CLD-05, CLD-06, CLD-07, CLD-08, CLD-09, CLD-11, CLD-12, TEST-02

## Acceptance criteria

- [ ] Request and response mapping is tested offline against golden JSON, including reasoning blocks sent back unchanged.
- [ ] Cache markers are sent at each boundary with the right lifetimes, and the volatile context uses the turn-scoped form when available.
- [ ] Every stop reason and error maps as in DESIGN.md §9; anything unrecognised is reported as unknown.
- [ ] Usage is priced from the shipped table, including cache writes by lifetime.
- [ ] A live recording replays offline exactly.
- [ ] An opt-in live test passes and shows cache reads on the second call of a turn.
