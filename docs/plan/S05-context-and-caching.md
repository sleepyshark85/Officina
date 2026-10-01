# S05 — Context and caching

**Milestone:** M2 · **Size:** M · **Depends on:** S04 · **Issue:** [#7](https://github.com/sleepyshark85/Officina/issues/7) · **Status:** todo

## Goal

Build each model request exactly as DESIGN.md §3 describes.

**Closes:** CTX-01, CTX-02, CTX-03, CTX-06, CTX-07, CTX-08, CTX-09, CTX-10, CTX-11, COST-01, INV-08, MSG-04, TEST-09

## Acceptance criteria

- [ ] All TEST-09 cases pass.
- [ ] The append-only check runs before every call, and a deliberately edited history fails it.
- [ ] Both forms of volatile context work: turn-scoped, and the appended form that sends only changes.
- [ ] Three cache boundaries are placed with their lifetimes, never more than the provider allows.
- [ ] Content from tools, documents and agents is labelled with its source.
- [ ] A low cache hit rate raises a warning event.

## Notes

How the boundaries map to Claude, and the live cache-hit check, are in S11.
