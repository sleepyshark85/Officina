# S09 — Triggers and admission

**Milestone:** M2 · **Size:** M · **Depends on:** S04, S08 · **Status:** todo

## Goal

Every way work can arrive, and the admission step in front of it.

**Closes:** TRG-01, TRG-02, TRG-03, TRG-04, ING-01, ING-02, ING-03, ING-04, ING-06, MDL-10, SEC-04, TEST-19

## Acceptance criteria

- [ ] The same agent definition behaves the same under every trigger.
- [ ] A batch reports a result per input, and one failure does not stop the others.
- [ ] Single requests and batches can run stateless.
- [ ] Masking is on by default and reversible; masked values reach only the tools allowed to see them.
- [ ] Rate limits apply, and a rejection is a normal result with a reason.
- [ ] An agent that has read untrusted content is marked.
