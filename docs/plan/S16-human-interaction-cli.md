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
- [x] `sof run` connects the tool servers and the Claude provider, and gives each agent a working copy and sandbox tools when their capabilities are on.

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

Part 2 (S16b) wired `sof run`:
- It connects the tool servers and registers the Claude provider, reading their secrets through the runner's `KnownSecrets`, so they
  are removed from what tools return (INV-06).
- With the workspace on, it opens the git workspace and registers the `workspace.*` tools (now with `delete_file` and `move_file`). With
  the sandbox on, it also registers the `sandbox.*` tools and the `extension:sandbox.commandRules` gate. They are `extension:` ids
  that `sof` registers, not `builtin:` ones, and a tool or gate of a capability that is off is an error (CAP-02).
- Each agent gets a working copy and its own `SandboxTools` when it first calls one of those tools (`ToolCall.Agent` tells a shared tool
  which agent called). They end with the run (SBX-03). The machine's sandbox is probed once, before the workspace opens, and a machine
  that cannot sandbox stops the command (SBX-07).
- `capabilities.workspace.baselineChecks` names checks from `checks`; `sof` resolves them from the checks its host registers (WS-02).
  `status` shows the integration queue's length and longest wait (WS-09).
