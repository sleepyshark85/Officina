# S20 — Team

**Milestone:** M6 · **Size:** M ×3 · **Depends on:** S13, S18, S19 · **Issue:** [#22](https://github.com/sleepyshark85/Officina/issues/22) · **Status:** done

## Goal

The lead, roles, parallel agents and the coding team preset.

**Closes:** TEAM-01, TEAM-02, TEAM-03, TEAM-04, TEAM-05, TEAM-06, TEAM-07, TEAM-08, TEAM-09, TEAM-10, CFG-05, CFG-11, TEST-26, TEST-29

## Acceptance criteria

- [x] The scripted team simulation (TEST-29) passes offline: plan, parallel work, review, integration, forced restart, report.
  *(Part 2: `TeamSimulationTests`, on real git and SQLite.)*
- [x] Agents never see each other's conversations; inter-agent messages are recorded and treated as data.
- [x] Helper agents respect depth, count, permission and budget limits. *(Part 3: `HelperTests`.)*
- [x] No agent can change any definition, permission, budget, rule or check (TEST-26). *(Part 3: `TamperTests`, through `sof run`
  on real git; part 1 leaves a task's checks, budget and review requirement to the owner, and the lead's authority to the team.)*
- [x] The three v1 presets ship. *(Part 3: `ExtendsAndPresetTests`.)*
- [x] An agent definition can build on another one and override parts of it (the coding team roles share a base). *(Part 3.)*

## Pieces

- **Part 1: the team runs** (TEAM-01 to TEAM-06, TEAM-08, TEAM-09, and RUN-06's whole-run pause): the team pattern over the task
  board, agents as instances of their roles, the lead's authority, messages between agents, statuses, and the follow-ups that
  need only the team: refusing turns on ended tasks, restricting agents' task edits, tying `memory.review` to the lead, the
  lead's retries, budgets per agent, and cost by definition.
- **Part 2: integration and the simulation** (TEST-29, and WS-02, WS-03, WS-09 and TASK-05 in the team): a working copy per
  task, integration through the queue with the command checks and `capabilities.workspace.baselineChecks`, a checkpoint after
  each integration, the CLI's board view and memory commands, and the scripted team simulation on real git and SQLite. A
  working copy of their own for fan-out branches of one agent that change files moved to S21 (see below).
- **Part 3: definitions, presets, helpers and sign-offs** (CFG-05, CFG-11, TEAM-07, TEAM-10, TEST-26): `extends`, the three
  presets, helper agents, the plan-approval sign-off, the hand-off tools (EGR-04), and the TEST-31 benchmark goals.

## Notes

Remove the "the team pattern cannot run yet" stub (S13), and run `samples/team` in `SampleTests` (TEST-06).

From earlier slices: add the CLI's board view (TASK-08, from S18); the command checks integration needs and the
`capabilities.workspace.baselineChecks` setting that names them (WS-02, from S06 and S16); a working copy per task, disposed
when the task ends (from S16); and a working copy of their own for fan-out branches of one agent that change files.

From S17: the CLI's owner commands for project memory: list the proposals, and approve or reject them, including the
condensing only the owner approves (until then the owner uses `AgentRunner.Memory`); and tie `memory.review` to the
lead role, so that only the team's lead can use it (S17 leaves that to the tool sets that hold it).

Write the TEST-31 benchmark goals and their hidden tests during this slice.

Part 1:
- `capabilities.team` turns the team on; it needs the task board (CAP-03), and an agent whose pattern is `team` needs it, as
  `team.*` tools do (CAP-02). The team lives in Core with the board it runs on, like the other capabilities so far; the
  `Sleepyshark.Officina.Team` project stays empty.
- A team is an agent's own pattern, never a step of another, so a run has at most one team and one board. Its lead and roles are
  agents that work in turns of their own; a role keeps no history, because each of its agents has a conversation of its own
  (TEAM-04). The lead is not also a role. The team agent's `budget.total` is refused: the run's budget is the team's, and the
  team's agents draw on it directly, not on the team agent's turn budget, so no default shadows the run budget's sign-off.
- Each agent of a team is an instance with an id the team gives it: the lead's name, or `role[n]` up to the role's `max`.
  `ToolContext.Instance` holds it, and `AgentId` (the instance, or the definition outside a team) is who acts: on the board, in
  the audit log, in events, in the record, in memory, and for tools (`ToolCall.Agent`). The definition still selects the
  settings. Events carry the id, so cost is broken down by agent and, new, by definition (`ByDefinition`, RUN-10).
- `TeamRun` works from the board as it is: with an empty board the lead plans; then each ready task goes to a free agent of
  its role (or the agent the lead assigned it to), which the team claims for it, and the agent works on it in a turn with the
  task as data; a task that needs a review goes to a free agent other than its author whose tools hold `tasks.review`, the
  lead last; a verified, approved task is done (with no workspace there is nothing to integrate). At most
  `pattern.maxParallel` agents work at once, the lead included (TEAM-03). Work that ends with its task unfinished, or with a
  handoff, a failure or the owner's stop, fails the task with the reason (`TaskBoard.FailAsync`, host only), and the lead is
  told of each failed task once, to retry, reassign, split and cancel, or leave it. When nobody works and nothing can start,
  the lead is asked once per board revision; if it changes nothing, the team hands off (no progress). When every task is done
  or cancelled, the lead reports, and that is the team's output. A lead turn that hands off ends the team (escalation).
- The lead's authority is `ToolContext.Lead`, which only the team sets, never a name: it assigns, retries and cancels tasks,
  and only it decides on memory proposals with `memory.review` (S17). No agent changes a task's checks, budget or review
  requirement. Any other agent changes only a task nobody has claimed, and of its own claimed task only whether it is blocked.
  `AgentRunner.RunAsync` refuses work on a task that is done, cancelled or failed.
- `builtin:team.message` sends another agent of the same team and run a message: a `messageSent` event with the sender and
  recipient, then the recipient's next model call has it as data from `agent:<id>` (TEAM-05, TEAM-06). It is a read tool:
  its effect stays with the team's own agents and is recorded. Inboxes, pauses and cancellations of a team's agents are keyed by
  run and id, so they never reach another run's agent of the same id, and they end with the team.
- TEAM-08: `agentStatusChanged` events: working (with the step), waiting (for a review, or for tasks' dependencies), idle,
  failed (with the reason), and finished when the team ends. Waiting for the owner is the `humanAsked` event. `sof run` prints both.
- RUN-06: `AgentRunner.PauseRun` and `ResumeRun` pause every agent of a run before its next model call; `Pause`, `Resume` and
  `Cancel` with a run and an id act on one agent of its team, whose task then goes back to the lead. At the `sof run` console,
  `pause` and `resume` without an agent act on the run, and agents of a team are named by their id.
- Budgets (from S19): each agent of a team has its own agent level from its definition's `budget.total`, with what it spent
  before a restart; the run's budget is shared by the team, in one process, so its time is the run's elapsed time inside its
  work items, as RUN-05 means. A resumed run's agent level now counts all the run's events, its steps' and team's included.
  When several agents run out of the run's budget at once, each asks the owner, and the yes extends the budget once: the
  extensions are read with the check that found the budget used up. `run.budget.tokens` and `run.budget.toolCalls` (unset by
  default, so cost and time stay the run's only limits) cap the run's tokens and tool calls, as RUN-05 asks of every level.
- SEC-04: a run's mark for untrusted content is one flag that every step and agent of the run shares and every gate check reads
  as it is, so an agent that reads untrusted content marks at once the agents already working, which its messages and the board
  can reach.
- The scripted model answers agents that run at once from scripts of their own (`ScriptedModelProvider.When`), matched by
  their work (`WorkOf`), so a parallel team is scripted deterministically.

Part 2:
- `IWorkspace.IntegrateAsync(copy, task, author)` goes through the workspace's one queue (WS-09). The team integrates a task in
  review once it is verified, approved if it needs a review, and nobody works on it; it is done, its copy closed, and a checkpoint
  taken at `integration` when the configuration lists it. A conflict or a failed baseline check returns the task to its author as
  a failed attempt (`ReturnAsync`, WS-03, TASK-09). The integration runs in the team's loop, one at a time as the queue does.
- On a conflict, the git workspace merges the baseline into the task's copy and commits it with the conflicts marked, so the
  author resolves them where it works and the next integration squashes from the merged baseline: one commit per task on the
  baseline, never a merge, attributed to the task and its author (WS-04). Verified on real git (`GitWorkspaceTests`).
- Each task has a working copy, `WorkingCopies.OfTask` (`<run>-task.<id>`; no agent's name has a dot), which the agents working on
  and reviewing it share; work for no task keeps the agent's own (`<run>-<agent id>`). `ToolCall.WorkingCopy` names the call's,
  and the host opens it when first needed. The copy goes when its task is done, or cancelled after an agent took it, and the
  rest when the run ends. `WorkspaceHost` is the run's `IWorkspace`, so closing a copy also stops its agents' background
  processes; each agent has its own sandbox tools in a copy, with its role's secrets (SBX-05, now by the instance's definition).
- `checks.<name>.command` is a check the host runs in the sandbox, in the working copy it checks: a task's when submitted
  (`IWorkingCopy.Directory`, TASK-05), or the change on the baseline (WS-02). It passes on exit code 0; its last 20 lines are the
  findings; it gets no secrets. It needs the sandbox and is refused on output, which has no working copy. It is stopped and
  fails after `checks.<name>.timeout` (20 minutes by default: a hanging test would otherwise hold the integration queue and the
  whole team). The submit tool's own time limit does not apply to it, so a check is never cut short or run again by the
  pipeline. A failed check's findings, at submit or at integration, mark the run as having read untrusted content (SEC-04).
- A change whose diff adds conflict markers (`<<<<<<< ` or `>>>>>>> ` lines) is still a conflict, so an author that submits
  again without resolving, or a reviewer that approves it, never puts markers on the baseline (WS-03). A file that really
  holds such lines, such as documentation about git, is refused as a conflict too; no case needs it yet.
- Cleaning up never loses work: the integration checkpoint is taken before the copy is closed; a copy that cannot be removed is
  a `warning` event and the team goes on (the next run removes leftovers); a scratch folder the queue could not remove keeps the
  result, with a warning, and is removed before the next integration.
- TASK-06: in a task's working copy only the task's assignee changes files with the `workspace.*` write tools; its reviewer
  reads them. This holds for those tools only: a `sandbox.*` command runs in the copy too, and what it changes cannot be told
  apart, so the reviewer of the coding team preset has no commands and no write tools (part 3). An application that gives a
  reviewer commands gives it the means to change the author's copy. Outside a team the same check applies to work for a task:
  an agent cannot change the task's copy until the task is claimed for it.
  `ToolCall.Definition` gives tools the agent's definition, which the sandbox's secrets are found by.
  `capabilities.workspace.baselineChecks` names the baseline's checks. What the sandbox set up for the scratch folder the
  baseline checks ran in is released before the folder goes.
- `sof run`'s console: `board` shows the run's board (TASK-08); `memory` lists the proposed changes to project memory, and
  `memory approve <n> [reason]` and `memory reject <n> <reason>` decide on them as the owner, condensing included (MEM-03, MEM-05).
- TEST-29: `TeamSimulationTests` wires a run as `sof` does (the workspace host, SQLite, command checks over the fake sandbox):
  plan, two developers at once in their tasks' copies, the tests in each copy, review, integration with the build and the tests
  on the baseline, a process that dies in the second review, a resume from the last checkpoint, and the report.
- From the part 1 review: the untrusted mark is set explicitly; the owner's yes extends the run's token and tool-call limits
  too (tested); a turn checks its cancellation before each model call, so a turn cancelled while paused starts no call once
  the pause is lifted. The flaky `TaskBoardTests.A_task_that_runs_out_of_budget_goes_back_to_the_lead` did not fail in 320 runs
  of it, 8 at a time, nor in 96 runs of its class; its cause is not found.

Part 3:
- CFG-05, CFG-04: the CLI's loader merges the files before binding (`ConfigurationFiles`): a file's `extends` lists presets
  (`preset:<id>`, embedded in Core) and other files, relative to it, each a layer below it once; objects merge key by key and a
  list or value in a higher file replaces the lower one's. An agent's `extends` names another agent it builds on, resolved after
  the files merge. Cycles, missing presets and missing agents are Merge-phase errors that `sof config validate` reports; the
  `extends` keys are never bound. `sof config show --origin` names the preset, the extended file, or "inherited by agents.X
  through extends". Environment variables and options are still bound by Microsoft.Extensions.Configuration.
- CFG-11: `preset:coding-team` (masking off, ING-02; the sign-offs planApproval, runBudgetExceeded and irreversibleAction,
  HITL-04; checks build and tests from `project.values`; roles built on a shared `coder`; the reviewer with no write tools and no
  commands), `preset:tool-using-assistant` and `preset:single-call-extractor`. A check's `command` may use `project.name` and
  `project.values.*` placeholders, validated like the instructions'.
- TEAM-07: `agents.<name>.helpers` lists the agents it may start with `builtin:team.start_helper`; `capabilities.team.helperDepth`
  (2) and `helperCount` (4 per turn) limit them. A helper is a nested turn, `helper[parent.n]`, with a fresh inbox and no history;
  `n` is numbered in the run, and a resumed run goes on after its events' numbers, so an id never repeats in a run. Its spending
  counts against the parent's turn budget, its permissions are within its parent's, and it has no tool its parent does not (a
  validation error otherwise). It keeps the parent's task, so the `workspace.*` write tools refuse it in the task's working copy
  (TASK-06), as they refuse a reviewer; a `sandbox.*` command is not refused, so a helper of a reviewer must have none, which the
  rule on tools gives when the reviewer has none. A helper must work in turns with the `none` history strategy.
- TEAM-10: `planApproval` asks the owner to approve the lead's plan before any task is dispatched; a denial goes back to the lead
  to re-plan, no answer within `run.approvalTimeout` hands the run off, and the `planApproved` event keeps a resumed run from asking
  again. A denial carries no reason; the owner says what to change with `tell lead <text>` at the `sof run` console.
- EGR-04: `builtin:human.request_handoff` ends the turn in a handoff to a human (reason `policyGap`) with the model's reason. A team
  agent's handoff already goes back to its lead, so `team.handoff` is not built (moved to S21, for a case).
- From the part 2 review: a non-command check run at submit is bounded by the submit tool's own `timeout`, which the tool takes
  when it is made (the submit tool is exempt from the pipeline's limit, so its command checks are never cut short).
- TEST-26: `TamperTests` runs an agent with file, command and task tools through `sof run`: `sof.json` (definitions, permissions,
  budgets, rules and checks), and every file in the workspace it extends, is read-only to its file tools and its commands, and a
  task's checks and budget are not its to change. No tool has access to the configuration (INV-10). The loader adds the extended
  files to `capabilities.workspace.protectedPaths` as read-only, so `sof config show` lists them.
- TEST-31: [`benchmark/`](../../benchmark/README.md) holds ten goals (four small, four medium, two larger), each a paragraph with a
  hidden acceptance test suite in Python's standard library that tests the built program from outside, and the `sof.json` every
  run uses. S21 runs them against the live model, and checks each suite against a reference solution first.

Moved to S21 from part 3:
- `team.handoff`, if a case needs a team agent to hand off to anyone but its lead.
- Running the TEST-31 benchmark against the live model.

Moved to S21:
- The per-run rate limit (ING-03) in a long-lived runner: no work joins a team's run from outside (its tasks are its own), so
  the limit still counts a run's first work and each resume; keeping it across processes belongs to the host and serve mode.
- `budget.total` of a pattern's step agents (a workflow's, a router's): they still draw on the entry agent's level. A team's
  agents have their own.
- The condition roots `checks.<name>`, `outcome` and `stopReason` (configuration reference §6): the team needs none of them.
- A task's tokens, time and tool calls (RUN-05): a task's budget caps its cost only.
- (From part 2.) A working copy of their own for fan-out branches of one agent that change files. Branches of one agent share
  its working copy while they run at once; a copy of its own needs to start from the agent's copy as it is and to say what
  becomes of its changes, which no case decides yet (the samples' branches only read).
- (From part 2.) `sof config validate` does not report what `sof run` refuses for an `extension:` tool, gate or check, which
  `sof` never registers (`baselineChecks` naming one is one more case): validation could report every `extension:` id `sof`
  does not provide.
