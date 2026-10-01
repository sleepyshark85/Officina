# S15 — Sandbox

**Milestone:** M5 · **Size:** M ×2 · **Depends on:** S00a, S14 · **Issue:** [#17](https://github.com/sleepyshark85/Officina/issues/17) · **Status:** todo

## Goal

Isolated command execution on Linux and on Windows.

**Closes:** TEST-01, SBX-01, SBX-02, SBX-03, SBX-04, SBX-05, SBX-06, SBX-07, SEC-01, TEST-11, TEST-25

## Acceptance criteria

- [ ] On both operating systems, the sandbox blocks file access outside the working copy, networking outside the allow list, and processes over their limits.
- [ ] Command rules allow, ask or deny, and anything unmatched is asked about.
- [ ] Background processes can be started, observed and stopped, and they stop when their owner ends.
- [ ] Output streams as events; the model gets a trimmed version and the full output is kept.
- [ ] Instructions planted in files, documents, command output, tool results and agent messages cannot exceed permissions (TEST-11).

## Notes

Do it as two pieces of work, Linux then Windows, following `docs/spikes/sandbox.md`. Still unproven after the spike: SBX-06, output caps, background processes, NuGet certificate-revocation hosts in the allow list (presets per toolchain). This slice also completes the test kit (TEST-01): S01 started it, S03 adds fake tools, S04 a controllable clock, S14 an in-memory workspace, and this slice the fake sandbox.
