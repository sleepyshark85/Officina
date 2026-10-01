# S18 — Task board

**Milestone:** M6 · **Size:** M · **Depends on:** S06, S08 · **Status:** todo

## Goal

Tasks, dependencies, verification and review.

**Closes:** TASK-01, TASK-02, TASK-03, TASK-04, TASK-05, TASK-06, TASK-07, TASK-08, TASK-09, TEST-20, TEST-21

## Acceptance criteria

- [ ] A task never reaches done while a verification check fails, whatever the agent says.
- [ ] Tasks never start before their dependencies, and circular dependencies are rejected.
- [ ] The reviewer is never the author, and every change to a task is recorded.
- [ ] The owner can edit the board at any time.
- [ ] A task that runs out of attempts or budget goes back to the lead.
