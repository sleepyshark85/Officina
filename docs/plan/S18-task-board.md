# S18 — Task board

**Milestone:** M6 · **Size:** M · **Depends on:** S06, S08 · **Issue:** [#20](https://github.com/sleepyshark85/Officina/issues/20) · **Status:** done

## Goal

Tasks, dependencies, verification and review.

**Closes:** TASK-01, TASK-02, TASK-03, TASK-04, TASK-05, TASK-06, TASK-07, TASK-08, TASK-09, TEST-20, TEST-21

## Acceptance criteria

- [x] A task never reaches done while a verification check fails, whatever the agent says.
- [x] Tasks never start before their dependencies, and circular dependencies are rejected.
- [x] The reviewer is never the author, and every change to a task is recorded.
- [x] The owner can edit the board at any time.
- [x] A task that runs out of attempts or budget goes back to the lead.

## Notes

- The board is a run's, in Core (`Tasks`), kept through `IStorage.Tasks` as changes keyed by (run, revision), with
  optimistic revisions like the run record. Each change holds the tasks it changed, who, when, what and why. Its rules
  are checked on the board after each change; the transitions are fixed in code, the task-states diagram's, so
  `transitions` is not a setting until a case needs another table. A task says itself whether it needs a review.
- Agents use `builtin:tasks.create`, `update`, `claim`, `submit_for_review` and `review`. They may not change a task's
  checks, budget, assignee or review requirement once it exists, nor cancel it. The owner and the host use
  `AgentRunner.Board`: add, edit, complete and return.
- Submitting runs the task's checks; only if they pass is the task in review. The host marks a task done once its
  change is integrated (`CompleteAsync`), or returns it to its author on a conflict or failed baseline check
  (`ReturnAsync`), which counts as a failed attempt (WS-03, TASK-05).
- A turn on a task (`Work.TaskId`) checks the task's budget, a budget level between the turn and the run (COST-02),
  and charges its cost to the task when it ends. Its volatile context shows the task's status and acceptance criteria
  (`context.currentTask`, CTX-01); `context.recordScope: task` limits the record to the task's entries (REC-06); and
  operating facts may use `{{work.id}}` and `{{work.task.id|title|status}}` (CTX-09).
- Gates and tools read the board, with the agent's task, through `GateContext.Board` and `ToolCall.Board` (TOOL-06).
  Status changes are events (`taskStatusChanged`, EVT-01), and model-call metrics carry `officina.task.id` (OBS-02).
- The SQLite format version is now 3, so a version-2 file, which has no `task_changes` table, is refused.

Left to later slices:
- S20: the team runtime claims and assigns tasks, gives each a working copy and passes it to the checks
  (`CheckContext.Directory`), integrates verified tasks through the queue, and calls `CompleteAsync` or
  `ReturnAsync`; agents of a team share one run, and so one board; `team` requires `taskBoard` (CAP-03); messages
  between agents as events.
- S16: the CLI's board view (TASK-08).
- S19: the agent budget level, and the hierarchy's escalation (RUN-05).
- Not built, for want of a case: the `requires-task-status` built-in gate and the `task` field of conditions
  (reference §5.6 and §6); an extension gate reads the board.
