# S16 — Human interaction and CLI

**Milestone:** M5 · **Size:** M · **Depends on:** S04, S08 · **Issue:** [#18](https://github.com/sleepyshark85/Officina/issues/18) · **Status:** done

## Goal

The owner in the loop, through the `sof` CLI.

**Closes:** HITL-01, HITL-02, HITL-03, HITL-04, HITL-05, HITL-06, LOOP-12, RUN-06, UX-01, TEST-27, TEST-01

## Acceptance criteria

- [x] All three permission modes work, and the mode can change during a run.
- [x] An approval pauses only the waiting agent. A changed version is checked again, and no answer by the timeout means deny.
- [x] The owner can message any agent; the agent can ask the owner a question and continue the same turn.
- [x] Sign-offs work, and unattended runs queue their questions.
- [x] The CLI shows each agent's status, what waits for the owner, and the cost so far.
- [x] Cancellation stops everything within the configured time.
- [x] The test kit has an in-memory workspace behind `IWorkspace`, the interface the `workspace.*` tools use (TEST-01, moved from S15).

## Notes

- Approvals reach the host's `IHumanChannel` whether or not `capabilities.humanInteraction` is on. The capability adds
  the `builtin:human.ask_owner` tool and the sign-offs; without it, an exhausted run budget hands off (RUN-05).
- `run.approvalTimeout` and `run.cancelWithin` are run settings, because approvals happen without the capability.
- Sign-offs are `runBudgetExceeded` and `irreversibleAction`. S20 adds plan approval with the lead's plan; integration
  needs no sign-off by default (HITL-04), and a tool's `approval` covers it when wanted.
- The pause and cancel controls act per agent on the runner (`Pause`, `Resume`, `Cancel`); the host's cancellation
  token cancels the whole run. A cancelled turn that does not stop within `run.cancelWithin` is left behind.
- The `workspace.*` tools (read, search, edit, write) live in Core over `IWorkingCopy`, so the test kit's
  `InMemoryWorkspace` drives them; `IWorkspace` has only opening and closing working copies until S18 (integration)
  and S19 (snapshots) need more.
- HITL-07 (a MAY: rating results) moved to S21.

Left to S16b (a second pull request, part of #18): wiring the workspace, sandbox and tool servers into `sof run` —
connecting the tool servers, a working copy and `SandboxTools` per agent disposed when it ends (SBX-03), the sandbox
tools and command rules gate as built-ins, probing the sandbox once at startup (SBX-07), the workspace and sandbox tools
only when their capability is on (CAP-02), `workspace.delete_file` and `workspace.move_file`, `baselineChecks` naming
checks (WS-02), and showing the integration queue (WS-09).
