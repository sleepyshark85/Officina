# S07 — History and conversation store

**Milestone:** M2 · **Size:** S · **Depends on:** S05 · **Issue:** [#9](https://github.com/sleepyshark85/Officina/issues/9) · **Status:** done

## Goal

Long conversations: shortening history, and conversations that survive restarts.

**Closes:** CTX-06, HIST-01, HIST-02, HIST-03, HIST-04, HIST-05, CAP-01, CAP-02, CAP-03, CAP-05, TEST-04, TEST-08, TEST-15

## Acceptance criteria

- [x] The history strategies `none`, `full`, `shortened` and `lastTurns` work (CTX-06, moved from S05).
- [x] Shortening is the only operation that changes sent history, and its result is validated before use.
- [x] "Input too long" leads to one shortening attempt, then a handoff.
- [x] A conversation reloaded after a restart continues with its full history.
- [x] Records survive shortening. Tasks and memory are added to this test in S18 and S17.
- [x] Capabilities switch on and off, with their dependencies checked; the conversation store is the first. A capability that is off adds no tools, storage or settings.
- [x] A missing capability and an unmet dependency are rejected with a message naming the setting (completes TEST-04).

## Notes

- A conversation is an agent's with one caller (tenant, agent, caller id), stored a turn at a time when the turn
  ends. A turn interrupted by a crash or cancellation is not stored; checkpoints (S19) cover the middle of a turn.
- Shortening happens only when the model reports the input too long, and only with `shortened`; `full` is how it
  is off. It may change earlier turns only, never the current one, so a single turn too long for the model is
  handed off. A shortened turn is appended holding the whole conversation, so the store stays append-only.
- `provider` shortening uses a model provider that implements `IHistoryShortener`; `extension:<id>` one the
  application registers with the runner. A shortener may clear old tool results to a note (HIST-05).
- Capabilities are an `enabled` flag per section, with a fixed dependency table in `CapabilitiesOptions`. The
  settings of a capability that is off are not checked. There is no per-agent narrowing: an agent's own settings
  already say which capabilities it uses.
- Anonymous callers share one conversation per agent and tenant.
- The SQLite format version is now 2, so a file written before the `conversations` table existed is refused.

Left to later slices:
- S06: the run record joins the shortening test (HIST-03, TEST-15). S17 and S18 add memory and tasks.
- S21: the Claude provider implements `IHistoryShortener` with Claude's own mechanism (CLD-06, moved from S11).
- S16, S17, S18, S19, S20: their capabilities add their switches and dependencies (checkpoints need the
  conversation store, the team needs the task board), and S16 offers the workspace and sandbox tools only when
  their capability is on.
