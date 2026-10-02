# S05 — Context and caching

**Milestone:** M2 · **Size:** M · **Depends on:** S04 · **Issue:** [#7](https://github.com/sleepyshark85/Officina/issues/7) · **Status:** done

## Goal

Build each model request exactly as DESIGN.md §3 describes.

**Closes:** CTX-01, CTX-02, CTX-03, CTX-08, CTX-09, CTX-10, CTX-11, COST-01, INV-08, MSG-04, TEST-09

## Acceptance criteria

- [x] All TEST-09 cases pass, except the memory change, which needs project memory (S17).
- [x] The append-only check runs before every call, and a deliberately edited history fails it.
- [x] Both forms of volatile context work: turn-scoped, and the appended form that sends only changes.
- [x] Cache boundaries are placed with their lifetimes, never more than the provider allows. Boundary ② follows
  project memory, so it arrives with S17.
- [x] Content from tools, documents and agents is labelled with its source.
- [x] A low cache hit rate raises a warning, in the turn's result; S08 publishes it as an event.

## Notes

How the boundaries map to Claude, and the live cache-hit check, are in S11.

Moved to the slices that add the state they need:
- CTX-06 (history strategy) to S07: every strategy but `none` needs the conversation store, and `shortened` needs
  shortening. Until then each turn starts a new conversation, which is `none`.
- CTX-07 (facts with their as-of time, read from the run record) to S06, which adds the run record. Until then the
  volatile context holds the operating facts, in their configured order.

Left to later slices:
- S17 (done): project memory in the prefix, boundary ②, and the TEST-09 case that a memory change does not edit a running
  conversation's prefix.
