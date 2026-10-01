# S07 — History and conversation store

**Milestone:** M2 · **Size:** S · **Depends on:** S05 · **Issue:** [#9](https://github.com/sleepyshark85/Officina/issues/9) · **Status:** todo

## Goal

Long conversations: shortening history, and conversations that survive restarts.

**Closes:** HIST-01, HIST-02, HIST-03, HIST-04, HIST-05, CAP-01, CAP-02, CAP-03, CAP-05, TEST-04, TEST-08, TEST-15

## Acceptance criteria

- [ ] Shortening is the only operation that changes sent history, and its result is validated before use.
- [ ] "Input too long" leads to one shortening attempt, then a handoff.
- [ ] A conversation reloaded after a restart continues with its full history.
- [ ] Records survive shortening. Tasks and memory are added to this test in S18 and S17.
- [ ] Capabilities switch on and off, with their dependencies checked; the conversation store is the first. A capability that is off adds no tools, storage or settings.
- [ ] A missing capability and an unmet dependency are rejected with a message naming the setting (completes TEST-04).
