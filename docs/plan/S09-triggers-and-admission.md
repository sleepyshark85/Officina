# S09 — Triggers and admission

**Milestone:** M2 · **Size:** M · **Depends on:** S04, S08 · **Issue:** [#11](https://github.com/sleepyshark85/Officina/issues/11) · **Status:** done

## Goal

Every way work can arrive, and the admission step in front of it.

**Closes:** TRG-01, TRG-02, TRG-03, TRG-04, ING-01, ING-02, ING-03, ING-04, ING-06, MDL-10, SEC-04, TEST-19

## Acceptance criteria

- [x] The same agent definition behaves the same under every trigger.
- [x] A batch reports a result per input, and one failure does not stop the others.
- [x] Single requests and batches can run stateless.
- [x] Masking is on by default and reversible; masked values reach only the tools allowed to see them.
- [x] Rate limits apply, and a rejection is a normal result with a reason.
- [x] An agent that has read untrusted content is marked.

## Notes

The host delivers work as a `Work` item (agent, input, trigger, caller, handoff flag, run id) to
`AgentRunner.RunAsync`, or a list of inputs to `RunBatchAsync`. Schedules, event sources and servers stay in the
host; the core only knows how the work arrived. `agents.<name>.triggers` limits the ways an agent takes work.

- Masking is a per-run token table in Core, not replaceable in v1, so DESIGN.md §4 has no `IMasker`. A provider runs
  its own tools, so `maskResults` and `receivesMaskedValues` are rejected on them; `untrusted` still marks the agent.
- Rate limits are fixed windows per owner and per tenant on the runner's `TimeProvider`.
- `storage.unstoredEvents` is unset by default, because the binder appends a configured list to a non-empty default.
- Handed over from earlier slices: the caller and the human-handoff flag arrive with the work (EGR-04, from S04);
  `caller.*` placeholders in operating facts (CTX-09, from S05); the host knows the run id before the run starts
  (from S08). `work.*` placeholders wait for work items with fields of their own, such as tasks (S18).

Parts of the closed requirements need state that later slices add:
- S19: the per-run rate limit (ING-03), once work can join a running run; long-running runs resume.
- Requests and batches are stateless under the default history strategy, `none` (TRG-04, CTX-06 from S07).
- S06: the run record receives content already masked (ING-02), as S07's stored history does.
- Kept history and masking together need the token table kept with the conversation (ING-06); see the plan's
  open follow-ups.
- S11: maps `ModelRequest.Batch` to Message Batches when the provider's `batch` feature is on (MDL-10, CLD-11).
- S20: the coding team preset turns masking off (ING-02).
