# S19 — Checkpoints and long runs

**Milestone:** M6 · **Size:** M ×2 · **Depends on:** S08, S14 · **Issue:** [#21](https://github.com/sleepyshark85/Officina/issues/21) · **Status:** doing (part 1 done)

## Goal

Runs that survive crashes, roll back, and last for days.

**Closes:** RUN-01, RUN-02, RUN-03, RUN-04, RUN-05, RUN-07, RUN-08, RUN-09, RUN-10, RUN-11, REL-03, TEST-12, TEST-23, TEST-24

## Acceptance criteria

- [x] A run killed at random points resumes with no lost checkpointed work and no repeated irreversible effect.
- [x] Rollback restores state and worktrees together, and lists the external effects it could not undo.
- [ ] The budget hierarchy escalates level by level: turn, task, agent, run.
- [ ] Cost is broken down by agent, definition, task, step and model, and a run report is produced at the end.

## Pieces

- **Part 1: checkpoints, resume and rollback** (RUN-01 status, RUN-02, RUN-03, RUN-04, RUN-07, RUN-08, RUN-09, REL-03, TEST-12,
  TEST-23, TEST-24).
- **Part 2: budgets, cost and the report** (RUN-05, RUN-10, RUN-11, and the rest of RUN-01), with what the Windows sandbox
  leaves behind and the per-run rate limit.

## Notes

From S17: a checkpoint records memory's revision (DESIGN.md §8), and a rollback resets it, so a conversation's prefix
revision and the revision it was told of stay valid.

Part 1:
- `capabilities.checkpoints` (`enabled`, `at`: `turn` by default, `step`, `integration`; it needs the conversation store).
  A run takes a checkpoint when it starts, where `at` says, and on demand (`AgentRunner.CheckpointAsync`, and `checkpoint` at
  the `sof run` console). A checkpoint holds positions: the stored turns of each conversation that keeps history, the
  revisions of the record and the board, the position in memory's log, the audit count and a commit per working copy.
  `ICheckpointStore` joins `IStorage`; the record, board, memory and conversation stores gained `Truncate`.
- `IWorkspace` gained `SnapshotAsync` and `RestoreAsync`. The git workspace commits changes not yet committed as a checkpoint
  commit on the copy's branch, and restores with `reset --hard` and `clean -fd`, opening a copy again from its branch, or from
  the commit when the branch is gone, and removing a copy that did not exist then. Integration squashes the checkpoint
  commits into the change's one commit. `OpenWorkingCopyAsync` returns a copy that is already open for the task.
- `RollbackAsync` truncates every store to the checkpoint, deletes later checkpoints, resets the working copies, sets the run
  back to running and returns the write-tool attempts made since that are outside the core's state (all but the built-in
  tools and `workspace.*`) in a `RollbackReport` and a `runRolledBack` event. `ResumeAsync` is a rollback to the last
  checkpoint, then the run's work starts again on the restored state; a run killed before its first checkpoint just starts
  again. Unfinished write-tool intents are flagged in a `runResumed` event (RUN-07). The irreversible-call rule of S03 needed
  no change: its intent is in the audit log, so the call goes to a human.
- Runs have a status (`IRunStore.RecordStatusAsync`): running from the start, then completed, failed, cancelled or waiting
  for a human (a handoff). A run whose process died stays running, and that is how resume finds it. `RunStarted` holds the
  work (input, trigger, task), so a stored run can start again. The event sequence is per run and continues from the stored
  log on resume.
- SQLite format version 5: `checkpoints` table, and the status and work columns of `runs`.
- `sof resume <run>`, `sof rollback <run> [--to <n>]` (lists the checkpoints without `--to`), and `sof run` prints its run id.
  Opening the git workspace removes what a dead run left (its worktrees, and the branches of runs that cannot resume).
- Left for part 2, or open: the budget already spent is not restored when a run resumes; the Windows sandbox leftovers
  (the AppContainer profile, the `%TEMP%\officina-<hash>` home folder, the read-and-execute grants on `toolchains` folders);
  budget warnings (S08); the per-run rate limit (ING-03, S09).
- Open, for a case: a pattern resumes from its first step, not part-way; masking tokens from before the crash are not restored,
  so a masked input is run as stored; only the caller's id and tenant are stored, so the host passes the `Caller` when it
  resumes; resume uses the runner's configuration, not the run's stored one; a rollback needs no turn running in the runner;
  a crashed run's branches are kept until it ends, and nothing lists runs yet. S20's team calls `CheckpointAsync` with
  `CheckpointPoint.Integration` after each integration.
