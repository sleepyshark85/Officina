# S20 — Team

**Milestone:** M6 · **Size:** M ×3 · **Depends on:** S13, S18, S19 · **Issue:** [#22](https://github.com/sleepyshark85/Officina/issues/22) · **Status:** doing

## Goal

The lead, roles, parallel agents and the coding team preset.

**Closes:** TEAM-01, TEAM-02, TEAM-03, TEAM-04, TEAM-05, TEAM-06, TEAM-07, TEAM-08, TEAM-09, TEAM-10, CFG-05, CFG-11, TEST-26, TEST-29

## Acceptance criteria

- [ ] The scripted team simulation (TEST-29) passes offline: plan, parallel work, review, integration, forced restart, report.
  *(Part 1: plan, parallel work, review, a forced restart and the lead's report, without a workspace; part 2 adds integration
  and the simulation on real git and SQLite.)*
- [x] Agents never see each other's conversations; inter-agent messages are recorded and treated as data.
- [ ] Helper agents respect depth, count, permission and budget limits. *(Part 3.)*
- [ ] No agent can change any definition, permission, budget, rule or check (TEST-26). *(Part 3; part 1 leaves a task's checks, budget and review requirement to the owner, and the lead's authority to the team.)*
- [ ] The three v1 presets ship. *(Part 3.)*
- [ ] An agent definition can build on another one and override parts of it (the coding team roles share a base). *(Part 3.)*

## Pieces

- **Part 1: the team runs** (TEAM-01 to TEAM-06, TEAM-08, TEAM-09, and RUN-06's whole-run pause): the team pattern over the task
  board, agents as instances of their roles, the lead's authority, messages between agents, statuses, and the follow-ups that
  need only the team: refusing turns on ended tasks, restricting agents' task edits, tying `memory.review` to the lead, the
  lead's retries, budgets per agent, and cost by definition.
- **Part 2: integration and the simulation** (TEST-29, and WS-02, WS-03, WS-09 and TASK-05 in the team): a working copy per
  task, integration through the queue with the command checks and `capabilities.workspace.baselineChecks`, a checkpoint after
  each integration, the CLI's board view and memory commands, and the scripted team simulation on real git and SQLite.
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
  When several agents run out of the run's budget at once, each asks the owner, and the yes extends the budget once.
- The scripted model answers agents that run at once from scripts of their own (`ScriptedModelProvider.When`), matched by
  their work (`WorkOf`), so a parallel team is scripted deterministically.
- Until part 2, the team cannot be on with the workspace: its changes would never reach the baseline.

Left to part 2 and part 3: see Pieces.

Moved to S21:
- The per-run rate limit (ING-03) in a long-lived runner: no work joins a team's run from outside (its tasks are its own), so
  the limit still counts a run's first work and each resume; keeping it across processes belongs to the host and serve mode.
- `budget.total` of a pattern's step agents (a workflow's, a router's): they still draw on the entry agent's level. A team's
  agents have their own.
- The condition roots `checks.<name>`, `outcome` and `stopReason` (configuration reference §6): the team needs none of them.
