# S04 — Turn loop

**Milestone:** M2 · **Size:** M · **Depends on:** S03 · **Issue:** [#6](https://github.com/sleepyshark85/Officina/issues/6) · **Status:** done

## Goal

The single-agent loop: stop reasons, stop conditions, budgets, stall detection, cancellation and handoffs.

**Closes:** LOOP-01, LOOP-02, LOOP-03, LOOP-04, LOOP-05, LOOP-06, LOOP-07, LOOP-09, LOOP-10, LOOP-11, MSG-02, MSG-05, MSG-06, MDL-07, MDL-09, INV-01, INV-07, EGR-01, EGR-02, EGR-03, EGR-04, COST-02, REL-02, CFG-12, TEST-03, TEST-14

## Acceptance criteria

- [x] Every stop reason and every stop condition has a test.
- [x] The turn budget is checked before every model call. When it runs out, the turn ends in a handoff built from recorded state.
- [x] The stall rule matches LOOP-07: exploration and edit–test–edit cycles are not stalls; repeated identical calls are.
- [x] Cancelling at any point gives every tool request a matching result.
- [x] Each agent processes one turn at a time, and work that arrives meanwhile waits.
- [x] Exceptions become results (failed or handed off), never crashes.
- [x] Model output is streamed, and usage is priced.
- [x] `sof config dry-run` validates a configuration, shows it, and runs an agent against scripted models.

## Notes

Parts of the closed requirements need state that later slices add:
- S06: the stop condition "the output passes its checks" (LOOP-05); accepted facts as progress (LOOP-07); facts, findings,
  decisions and citations in handoffs and results (EGR-01, EGR-02).
- S07: one history shortening before "input too long" hands off (LOOP-03, HIST-04).
- S09: the caller and a handoff flag arrive with the work (EGR-04); until then runs act for an anonymous caller.
- S14: working-copy changes as progress (LOOP-07). S16: a tool the model calls to hand off to a human (EGR-04).
- S18 and S19: the task and agent budget levels, and their hierarchy (COST-02, RUN-05).
