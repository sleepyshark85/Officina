# S07 — History and conversation store

**Milestone:** M2 · **Size:** S · **Depends on:** S05 · **Status:** todo

## Goal

Long conversations: shortening history, and conversations that survive restarts.

**Closes:** HIST-01, HIST-02, HIST-03, HIST-04, HIST-05, CAP-05, TEST-15

## Acceptance criteria

- [ ] Shortening is the only operation that changes sent history, and its result is validated before use.
- [ ] "Input too long" leads to one shortening attempt, then a handoff.
- [ ] A conversation reloaded after a restart continues with its full history.
- [ ] Records survive shortening. Tasks and memory are added to this test in S18 and S17.
