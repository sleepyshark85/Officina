# S06 — Run record and output

**Milestone:** M2 · **Size:** M · **Depends on:** S04 · **Status:** todo

## Goal

The shared run record, and checked output.

**Closes:** REC-01, REC-02, REC-03, REC-04, REC-05, REC-06, OUT-01, OUT-02, OUT-03, OUT-04, OUT-05, INV-09, CONC-01

## Acceptance criteria

- [ ] Record tools propose changes; the core validates them and applies each one atomically with a new revision.
- [ ] Conflicting values are both kept and reported.
- [ ] Concurrent writers never overwrite each other silently (stress test).
- [ ] Invalid structured output is retried twice, then handed off.
- [ ] Output checks run in order, and the first failure decides.
- [ ] Citation rules work in all three modes: off, resolve and required.
- [ ] Results can carry artifacts.
