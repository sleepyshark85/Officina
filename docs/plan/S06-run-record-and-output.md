# S06 — Run record and output

**Milestone:** M2 · **Size:** M · **Depends on:** S04, S05 · **Issue:** [#8](https://github.com/sleepyshark85/Officina/issues/8) · **Status:** done

## Goal

The shared run record, and checked output.

**Closes:** REC-01, REC-02, REC-03, REC-04, REC-05, REC-06, CTX-07, OUT-01, OUT-02, OUT-03, OUT-04, OUT-05, INV-09, CONC-01

## Acceptance criteria

- [x] Record tools propose changes; the core validates them and applies each one atomically with a new revision.
- [x] Conflicting values are both kept and reported.
- [x] Concurrent writers never overwrite each other silently (stress test).
- [x] Invalid structured output is retried twice, then handed off.
- [x] Output checks run in order, and the first failure decides.
- [x] The stop condition "the output passes its checks" completes a turn (LOOP-05, left over from S04).
- [x] Citation rules work in all three modes: off, resolve and required.
- [x] Results can carry artifacts.
- [x] The record's facts join the volatile context in a consistent order, each with its as-of time, and building the
  input never changes the record (CTX-07, moved from S05).

## Notes

- Agents propose through the built-in tools `builtin:record.propose_fact`, `propose_finding`, `propose_decision` and
  `cite`. The core validates each proposal against the record and appends it with the next revision; the store refuses
  a taken revision, and the core then validates again (REC-04). Answers and entries cite as `[cite:<id>]`, where the id
  is any text without `]`. Retrieved passages join the record as citations by their source's id, before the turn and
  through `knowledge:` tools, so the default `resolve` rule accepts answers that cite them (OUT-04).
- The volatile context shows facts, findings and decisions by default (CTX-01); citations only when `context.record`
  lists them, so a retrieved passage is not sent twice.
- A trimmed tool result is kept in full as an artifact, which `builtin:artifact.page` reads (TOOL-09, SBX-04). Gates get
  the run record (TOOL-06). The `officina.checks` metric gives check pass rates (OBS-02).
- An accepted proposal is a new call, and an identical proposal is not added again, so the stall rule needs nothing more
  (LOOP-07). The result carries the record and the artifacts (EGR-01, EGR-02).
- The record and artifact tables share SQLite format version 2 with S07's conversations.

Left to later slices:
- S13: the check-failure outcome "revise", where the pattern supports it (OUT-03); until then a failure hands off.
- S21: send the output schema to providers that support structured output natively (CLD-06, moved from S11).
- S20: `checks` takes `extension:` checks only; S20 adds the command checks that integration needs and the
  `capabilities.workspace.baselineChecks` setting that names them (WS-02).
- S18: `task` record scope (REC-06), the task board in `GateContext` (TOOL-06), task verification checks.
