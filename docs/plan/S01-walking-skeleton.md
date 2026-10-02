# S01 — Walking skeleton

**Milestone:** M1 · **Size:** M · **Depends on:** none · **Issue:** [#3](https://github.com/sleepyshark85/Officina/issues/3) · **Status:** done

## Goal

Run the thinnest end-to-end path, configuration → turn → scripted model → result, in the final solution structure.

## Scope

- **Out:** Configuration files (S02) and tools (S03).

**Closes:** MDL-01, MSG-01, MSG-03, CFG-03, CAP-04, TEST-32

## Acceptance criteria

- [x] The solution has every project from DESIGN.md §1 (empty where not needed yet), and it builds and tests on Linux and Windows in GitHub Actions.
- [x] A test runs an agent with only `instructions` against a scripted model and gets a completed result with the output.
- [x] The dependency check fails the build when a project other than the Claude provider references the Anthropic SDK (shown by a negative test).
- [x] Message types are immutable.

## Notes

The test kit starts here and grows in every slice. TEST-01 closes in S15, when its last part (the fake sandbox) exists.
