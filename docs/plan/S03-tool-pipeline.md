# S03 — Tool pipeline

**Milestone:** M2 · **Size:** M · **Depends on:** S02 · **Status:** todo

## Goal

Every tool call goes through one governed path (DESIGN.md §5).

**Closes:** TOOL-01, TOOL-02, TOOL-03, TOOL-04, TOOL-05, TOOL-06, TOOL-07, TOOL-08, TOOL-09, TOOL-10, TOOL-11, TOOL-13, LOOP-08, MSG-07, INV-02, INV-03, INV-04, INV-05, INV-06, INV-10, ING-05, SEC-05, TEST-10, TEST-13

## Acceptance criteria

- [ ] The steps run in the order of DESIGN.md §5, and the first that does not allow the call decides. There is a test per row.
- [ ] Every write-tool attempt is audited, including denied, asked and failed ones.
- [ ] Irreversible tools record their intent before running; a recorded intent with no outcome is never re-run.
- [ ] Errors reach the model as a category and a short message only. A planted secret never appears in model input, logs or errors.
- [ ] Calls marked safe run in parallel, and their results come back in the order requested.
- [ ] Provider server-side tools are enabled only explicitly and are audited after the fact.
- [ ] A write tool without a gate fails validation.

## Notes

The crash-and-resume half of TOOL-10 is tested in S19 (TEST-12).
