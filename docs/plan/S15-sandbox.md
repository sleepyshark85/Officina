# S15 — Sandbox

**Milestone:** M5 · **Size:** M ×2 · **Depends on:** S00a, S14 · **Issue:** [#17](https://github.com/sleepyshark85/Officina/issues/17) · **Status:** done

## Goal

Isolated command execution on Linux and on Windows.

**Closes:** SBX-01, SBX-02, SBX-03, SBX-04, SBX-05, SBX-06, SBX-07, SEC-01, TEST-11, TEST-25

## Acceptance criteria

- [x] On both operating systems, the sandbox blocks file access outside the working copy, networking outside the allow list, and processes over their limits.
- [x] Command rules allow, ask or deny, and anything unmatched is asked about.
- [x] Background processes can be started, observed and stopped, and they stop when their owner ends.
- [x] Output streams as events; the model gets a trimmed version and the full output is kept. *(Each line is published as a `toolOutput` event, with the proxy's decisions among them; the tool pipeline trims what the model gets, and S06 keeps the full result as an artifact.)*
- [x] Instructions planted in files, documents, command output, tool results and agent messages cannot exceed permissions (TEST-11).

## Pieces

- **Linux ([#36](https://github.com/sleepyshark85/Officina/pull/36)):** what both operating systems share — `ISandbox`
  in Core, the `capabilities.sandbox` settings, the command rules gate, the `sandbox.*` tools with background
  processes, the filtering proxy, and the test kit's fake sandbox — and `LinuxSandbox` (bubblewrap,
  `systemd-run --user`, socat). Protected paths are kept out of commands with mounts.
- **Windows:** `WindowsSandbox` (an AppContainer per working copy, a job object per command), the proxy over a named
  pipe with a PowerShell forwarder inside the sandbox, protected paths kept out by withdrawing the container's grant,
  toolchains outside the system folders on both operating systems, and command output and proxy decisions as events.

## Notes

Do it as two pieces of work, Linux then Windows, following `docs/spikes/sandbox.md`. Still unproven after the spike: SBX-06, output caps, background processes, NuGet certificate-revocation hosts in the allow list (presets per toolchain). The Linux piece proved SBX-06, output caps and background processes on Linux.

TEST-01 moved to S16. Its last parts are the fake sandbox, which this slice adds, and the in-memory workspace, which needs `IWorkspace` in Core. The sandbox needs only the working copy's folder, so `IWorkspace` arrives with its first user, the `workspace.*` tools in S16.
