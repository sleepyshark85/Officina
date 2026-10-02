# S19 — Checkpoints and long runs

**Milestone:** M6 · **Size:** M · **Depends on:** S08, S14 · **Issue:** [#21](https://github.com/sleepyshark85/Officina/issues/21) · **Status:** todo

## Goal

Runs that survive crashes, roll back, and last for days.

**Closes:** RUN-01, RUN-02, RUN-03, RUN-04, RUN-05, RUN-07, RUN-08, RUN-09, RUN-10, RUN-11, REL-03, TEST-12, TEST-23, TEST-24

## Acceptance criteria

- [ ] A run killed at random points resumes with no lost checkpointed work and no repeated irreversible effect.
- [ ] Rollback restores state and worktrees together, and lists the external effects it could not undo.
- [ ] The budget hierarchy escalates level by level: turn, task, agent, run.
- [ ] Cost is broken down by agent, definition, task, step and model, and a run report is produced at the end.

## Notes

From S17: a checkpoint records memory's revision (DESIGN.md §8), and a rollback resets it, so a conversation's prefix
revision and the revision it was told of stay valid.
