# S04 — Turn loop

**Milestone:** M2 · **Size:** M · **Depends on:** S03 · **Issue:** [#6](https://github.com/sleepyshark85/Officina/issues/6) · **Status:** todo

## Goal

The single-agent loop: stop reasons, stop conditions, budgets, stall detection, cancellation and handoffs.

**Closes:** LOOP-01, LOOP-02, LOOP-03, LOOP-04, LOOP-05, LOOP-06, LOOP-07, LOOP-09, LOOP-10, LOOP-11, MSG-02, MSG-05, MSG-06, MDL-07, MDL-09, INV-01, INV-07, EGR-01, EGR-02, EGR-03, EGR-04, COST-02, REL-02, CFG-12, TEST-03, TEST-14

## Acceptance criteria

- [ ] Every stop reason and every stop condition has a test.
- [ ] The turn budget is checked before every model call. When it runs out, the turn ends in a handoff built from recorded state.
- [ ] The stall rule matches LOOP-07: exploration and edit–test–edit cycles are not stalls; repeated identical calls are.
- [ ] Cancelling at any point gives every tool request a matching result.
- [ ] Each agent processes one turn at a time, and work that arrives meanwhile waits.
- [ ] Exceptions become results (failed or handed off), never crashes.
- [ ] Model output is streamed, and usage is priced.
- [ ] `sof config dry-run` validates a configuration, shows it, and runs an agent against scripted models.
