# S00a — Sandbox spike

**Milestone:** M0 · **Size:** S · **Depends on:** none · **Issue:** [#1](https://github.com/sleepyshark85/Officina/issues/1) · **Status:** done

## Goal

Prove that commands can be isolated with filtered networking on Linux and Windows, before the sandbox work is planned in detail.

## Scope

- **In:** Linux: bubblewrap, cgroups v2, empty network namespace, proxy over a Unix socket. Windows: AppContainer, Job Object, loopback proxy.
- **Out:** Production code and tool integration (S15).

**Closes:** nothing (a spike: findings only)

## Acceptance criteria

- [ ] On Linux and Windows, a prototype runs `dotnet build` inside a working copy.
- [ ] Reading a file outside the working copy fails.
- [ ] A host on the allow list is reachable through the filtering proxy; any other host is not.
- [ ] CPU, memory and time limits stop a runaway process.
- [ ] `docs/spikes/sandbox.md` records what worked, what needs admin rights, and a go/no-go per OS with a fallback (for example containers).

## Notes

Highest technical risk in the design. Informs SBX-07 and the M5 dates.
