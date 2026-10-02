# S19 — Checkpoints and long runs

**Milestone:** M6 · **Size:** M ×2 · **Depends on:** S08, S14 · **Issue:** [#21](https://github.com/sleepyshark85/Officina/issues/21) · **Status:** done

## Goal

Runs that survive crashes, roll back, and last for days.

**Closes:** RUN-01, RUN-02, RUN-03, RUN-04, RUN-05, RUN-07, RUN-08, RUN-09, RUN-10, RUN-11, REL-03, TEST-12, TEST-23, TEST-24

## Acceptance criteria

- [x] A run killed at random points resumes with no lost checkpointed work and no repeated irreversible effect.
- [x] Rollback restores state and worktrees together, and lists the external effects it could not undo.
- [x] The budget hierarchy escalates level by level: turn, task, agent, run.
- [x] Cost is broken down by agent, definition, task, step and model, and a run report is produced at the end. *(By agent, task, step and model; "definition" is the agent until S20.)*

## Pieces

- **Part 1: checkpoints, resume and rollback** (RUN-01 status, RUN-02, RUN-03, RUN-04, RUN-07, RUN-08, RUN-09, REL-03, TEST-12,
  TEST-23, TEST-24).
- **Part 2: budgets, cost and the report** (RUN-05, RUN-10, RUN-11, and the rest of RUN-01), with what the Windows sandbox
  leaves behind and the per-run rate limit.

## Notes

From S17: a checkpoint records memory's log position (DESIGN.md §8). A rollback does not reset memory, which runs share, so a
conversation's prefix revision and the revision it was told of stay valid; the report lists the changes made since.

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
- `RollbackAsync` truncates the conversations, record and board to the checkpoint, deletes later checkpoints, resets the working
  copies, sets the run back to running and returns the write-tool attempts made since that are outside the core's state (all but
  the built-in tools and `workspace.*`) in a `RollbackReport` and a `runRolledBack` event. Conversation turns carry their run
  id, and a rollback or resume is refused when another run wrote to the conversation since the checkpoint. Memory is shared, so
  it is left alone and the report lists the changes since. `ResumeAsync` is a rollback to the last
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
- Left after part 2: a resumed pattern runs again from its first step, redoing the steps already done, so `at: step` buys
  little until a pattern resumes part-way (it needs each step's output kept, and the patterns to skip steps that finished);
  after a squash or a branch's deletion old checkpoint commits live only in the reflog, so `git gc` can break restoring them.
Part 2:
- Budgets form a hierarchy: turn, pattern (the agent's turn budget), agent, run, with the task's cost between the turn and the
  run (S18). `agents.<name>.budget.total` caps the agent's tool calls, tokens, cost and time over all its turns in a run
  (default $25, 8 hours, 100M tokens, 10,000 tool calls). A level that runs out ends the turn in a handoff that names it, and the
  work goes to the level above, as before: the task goes back to the lead, a pattern's step to its pattern, and the run
  budget asks the owner, or hands off without one. Each level warns once at 80% of a limit with a `budgetWarning` event
  (EVT-01); `sof run` prints it.
- A resumed run has spent what it had. `Spent.Of` reads the run's stored events: the cost and tokens of `modelCallEnded`, the
  `toolCallEnded` count, and the time the run was running, which leaves out the time before a `runResumed` event, so the
  downtime between a crash and its resume does not count. With checkpoints on, `modelCallEnded`, `toolCallEnded` and
  `runResumed` cannot be left unstored. The agent's time is the run's. An owner's sign-off to go on past the run budget is not
  kept: after a resume the owner is asked again.
- `ModelCallEnded` names the model that served the call and the task. `CheckRan` is a new event for each check run. `RunReport`
  (`AgentRunner.ReportAsync`, `sof report <run>`) reads what is stored: outcome, time running, cost by agent, task, step and
  model, the tasks, the decisions, the checks, and the open issues (an unfinished run, a handoff, tasks not done, conflicts in
  the record, checks that never passed). `sof run` and `sof resume` print it when they end.
- `policies.rateLimits.perRun` is a fixed window per run id. Work enters a run only by its first work item and each resume today.
- The conversation store's `TruncateAsync` takes the run and deletes nothing when another run's turns come after the
  position, as one statement, and the rollback counts the turns after it (a race between the check and the deletion).
  `sof resume`, `rollback` and `report` print an `error:` line for a database in another format version.
- `sof` holds `.sof/<run>.lock` while it works on a run, so `resume` and `rollback --to` refuse a run that another process
  holds, with the workspace off too.
- `ISandbox.Release(directory, toolchains)` is called when a working copy's sandbox tools are disposed, and for each leftover
  working copy removed when the workspace opens. The Windows sandbox removes the container's grants on the toolchains, its
  home folder and its AppContainer profile; Linux has nothing outside the copy. The probe's fixed profile and folder stay (one per
  machine).
- Not done, and on purpose: the event-log read that seeds an event sequence reads the whole log (it is also what the budget
  reads, once per resume).

- Open, for a case: masking tokens from before the crash are not restored,
  so a masked input is run as stored; only the caller's id and tenant are stored, so the host passes the `Caller` when it
  resumes; resume uses the runner's configuration, not the run's stored one; a rollback needs no turn running in the runner;
  a crashed run's branches are kept until it ends, and nothing lists runs yet. S20's team calls `CheckpointAsync` with
  `CheckpointPoint.Integration` after each integration.
