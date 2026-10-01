# S16 — Human interaction and CLI

**Milestone:** M5 · **Size:** M · **Depends on:** S04, S08 · **Issue:** [#18](https://github.com/sleepyshark85/Officina/issues/18) · **Status:** todo

## Goal

The owner in the loop, through the `sof` CLI.

**Closes:** HITL-01, HITL-02, HITL-03, HITL-04, HITL-05, HITL-06, HITL-07, LOOP-12, RUN-06, UX-01, TEST-27, TEST-01

## Acceptance criteria

- [ ] All three permission modes work, and the mode can change during a run.
- [ ] An approval pauses only the waiting agent. A changed version is checked again, and no answer by the timeout means deny.
- [ ] The owner can message any agent; the agent can ask the owner a question and continue the same turn.
- [ ] Sign-offs work, and unattended runs queue their questions.
- [ ] The CLI shows each agent's status, what waits for the owner, and the cost so far.
- [ ] Cancellation stops everything within the configured time.
- [ ] The test kit has an in-memory workspace behind `IWorkspace`, the interface the `workspace.*` tools use (TEST-01, moved from S15).
