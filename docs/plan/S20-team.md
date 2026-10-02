# S20 — Team

**Milestone:** M6 · **Size:** M · **Depends on:** S13, S18, S19 · **Issue:** [#22](https://github.com/sleepyshark85/Officina/issues/22) · **Status:** todo

## Goal

The lead, roles, parallel agents and the coding team preset.

**Closes:** TEAM-01, TEAM-02, TEAM-03, TEAM-04, TEAM-05, TEAM-06, TEAM-07, TEAM-08, TEAM-09, TEAM-10, CFG-05, CFG-11, TEST-26, TEST-29

## Acceptance criteria

- [ ] The scripted team simulation (TEST-29) passes offline: plan, parallel work, review, integration, forced restart, report.
- [ ] Agents never see each other's conversations; inter-agent messages are recorded and treated as data.
- [ ] Helper agents respect depth, count, permission and budget limits.
- [ ] No agent can change any definition, permission, budget, rule or check (TEST-26).
- [ ] The three v1 presets ship.
- [ ] An agent definition can build on another one and override parts of it (the coding team roles share a base).

## Notes

Remove the "the team pattern cannot run yet" stub (S13), and run `samples/team` in `SampleTests` (TEST-06).

From earlier slices: add the CLI's board view (TASK-08, from S18); the command checks integration needs and the
`capabilities.workspace.baselineChecks` setting that names them (WS-02, from S06 and S16); a working copy per task, disposed
when the task ends (from S16); and a working copy of their own for fan-out branches of one agent that change files.

From S17: the CLI's owner commands for project memory: list the proposals, and approve or reject them, including the
condensing only the owner approves (until then the owner uses `AgentRunner.Memory`); and tie `memory.review` to the
lead role, so that only the team's lead can use it (S17 leaves that to the tool sets that hold it).

Write the TEST-31 benchmark goals and their hidden tests during this slice.
