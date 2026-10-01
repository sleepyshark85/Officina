# S14 — Git workspace

**Milestone:** M5 · **Size:** M · **Depends on:** S03 · **Issue:** [#16](https://github.com/sleepyshark85/Officina/issues/16) · **Status:** done

## Goal

The baseline, working copies, and the integration queue.

**Closes:** WS-01, WS-02, WS-03, WS-04, WS-05, WS-06, WS-07, WS-08, WS-09, RUN-12, TEST-22

## Acceptance criteria

- [x] Each agent's changes stay invisible to other agents until integrated.
- [x] Integration goes through the queue and runs the baseline checks.
- [x] Two agents editing the same file produce a detected conflict, never a silent overwrite.
- [x] An edit fails if the file changed since it was read.
- [x] Protected paths are hidden or read-only.
- [x] A second run on the same workspace is refused.
