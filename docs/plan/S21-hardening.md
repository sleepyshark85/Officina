# S21 — Hardening and benchmark

**Milestone:** M7 · **Size:** M · **Depends on:** S20 · **Issue:** [#23](https://github.com/sleepyshark85/Officina/issues/23) · **Status:** todo

## Goal

Prove the non-functional targets and the coding team's success rate.

**Closes:** CLD-06, CLD-11, SCALE-01, SCALE-02, SCALE-03, LAT-01, LAT-02, SEC-03, TEST-30, TEST-31, TEST-33, HITL-07

## Acceptance criteria

- [ ] Automated load tests measure SCALE-02, LAT-01 and LAT-02, and the targets are met.
- [ ] The coding team benchmark reaches at least 90% across all runs, on Linux and Windows.
- [ ] Coverage of the core is at least 85%.
- [ ] Every MUST is verified by a test or a recorded review (v1 acceptance).

## Notes

- HITL-07 (a MAY: end users rate a result, linked to its run) moved here from S16: build it if time allows, or record
  it as not in v1.

From S17: restore masked values in text that agents propose for project memory (memory is durable, and masking tokens
are per run) or refuse proposals that contain a token, when masking is on.

From S11, each a feature switch of the Claude provider:
- CLD-06: native structured output (`output_config.format`, `strict` tools), compaction as the provider's
  `IHistoryShortener` (HIST-01), clearing old tool results, task budgets and the server-side refusal fallback.
- CLD-11: `ModelRequest.Batch` sent through Message Batches (MDL-10).

From S20 (part 1):
- The per-run rate limit (ING-03) kept across processes by a long-lived runner, with the host and serve mode. A team's tasks
  are its own work, so nothing joins a live run from outside.
- `budget.total` of a pattern's step agents, which still draw on the entry agent's level (a team's agents have their own).
- The condition roots `checks.<name>`, `outcome` and `stopReason` (configuration reference §6), for a case that needs them.
- RUN-05: a task's tokens, time and tool calls; its budget caps its cost only. (S20 built the run's tokens and tool calls.)

From S20 (part 2):
- A working copy of their own for fan-out branches of one agent that change files: from the agent's copy as it is, with a rule
  for what becomes of each branch's changes. Until then branches of one agent share its working copy.
- A time limit for command checks, so a hanging command cannot hold the integration queue.
