# Spike S00a — Sandbox

Prototype: `spikes/sandbox/SandboxSpike` (one .NET 10 console, OS-specific code in
`LinuxSandbox.cs` / `WindowsSandbox.cs`, shared filtering proxy in `FilterProxy.cs`).
Run with `dotnet build spikes/sandbox/SandboxSpike && dotnet spikes/sandbox/SandboxSpike/bin/Debug/net10.0/SandboxSpike.dll --report out.md`.
CI: `.github/workflows/sandbox-spike.yml` (ubuntu-latest + windows-latest, spike branch only).

## Linux

Proved on a developer machine (Linux Mint 22.3 / Ubuntu 24.04 base, kernel 6.17, cgroups v2,
16 cores), as an ordinary user. **ubuntu-latest CI result: pending** (see "Not yet proven").

| Check | Result | How | Root? |
|---|---|---|---|
| `dotnet build` + run in working copy | PASS | bwrap `--unshare-all`; only `/usr` (read-only, holds the SDK), a few `/etc` files, `/proc`, `/dev`, tmpfs `/tmp` and the working copy (read-write) are mounted; `HOME`, `NUGET_PACKAGES`, `DOTNET_CLI_HOME` point into the working copy | no |
| Read file outside working copy (`~/secret`) | PASS (blocked) | not mounted: path does not exist inside | no |
| Write outside working copy | PASS (blocked) | same | no |
| Direct network (DNS + raw IP) | PASS (blocked) | empty network namespace (only `lo`) | no |
| Allowed host (api.nuget.org) via proxy | PASS (HTTP 200) | host proxy on a Unix socket, bind-mounted in; `socat` inside forwards `127.0.0.1:3128` to it; `HTTPS_PROXY` set | no |
| Other host (example.com) via proxy | PASS (403) | proxy allow list on the CONNECT host | no |
| NuGet restore + build through proxy | PASS | real `dotnet build` of a project with a package reference | no |
| Memory limit (256 MB) | PASS (OOM-killed at 256 MB) | `systemd-run --user --scope -p MemoryMax -p MemorySwapMax=0 -p OOMPolicy=continue` → cgroup `memory.max`; `memory.events oom_kill 1` | no* |
| Process limit (32) | PASS (fork fails with EAGAIN after 27 children) | `TasksMax` → `pids.max` | no* |
| CPU limit (0.5 core) | PASS (0.51 cores used vs 15.9 unlimited) | `CPUQuota=50%` → `cpu.max` | no* |
| Wall-time limit (3 s) | PASS (killed at 3.2 s, 0 processes left) | host timer + write `1` to the scope's `cgroup.kill` | no |
| Cancel kills everything | PASS (11 processes incl. `setsid`, double-fork, `nohup` — 0 left) | `cgroup.kill` on the scope; bwrap pid namespace + `--die-with-parent` as a second line | no |

\* Needs a systemd **user manager** for the account (normal for a logged-in desktop user; for a
service account, `loginctl enable-linger <user>` once, as root). systemd then delegates
`cpu memory pids` to the user without root.

**Go** for Linux, with the bubblewrap + cgroups v2 design as written.

Findings worth carrying into S15:

- A Unix socket path is limited to 108 bytes (`sun_path`); put the proxy socket in `$XDG_RUNTIME_DIR`, not under the working copy.
- NuGet package-signature verification also contacts `crl3.digicert.com` / `ocsp.digicert.com` (plain HTTP). With them denied the restore still succeeded without warnings here, but the allow list for "NuGet" should either include those hosts or set `NUGET_CERT_REVOCATION_MODE=offline`. Expect similar side hosts for other toolchains.
- The cgroup is gone the moment the last process exits; read `memory.events` / `pids.events` while it runs (or use `OOMPolicy=continue` and a short settle) to report *why* a command died.
- `socat` must be present on the host (it runs inside from the read-only `/usr`). A ~50-line .NET forwarder would remove that dependency but costs a runtime start per command.
- Ubuntu ≥ 23.10 restricts unprivileged user namespaces through AppArmor (`kernel.apparmor_restrict_unprivileged_userns=1`); bwrap then needs an AppArmor profile granting `userns` (root, one-time). This machine had the restriction off, so that path is only exercised in CI.

## Windows

**Result: pending — not yet run.** The prototype (`WindowsSandbox.cs`: AppContainer profile with
zero capabilities, working copy ACL'd to the container SID, Job Object with
`JobMemoryLimit`, `ActiveProcessLimit`, CPU-rate hard cap and `KILL_ON_JOB_CLOSE`,
`TerminateJobObject` on timeout/cancel) compiles but has not executed: pushing the CI workflow was
rejected because the GitHub token lacks the `workflow` scope. No local Windows machine exists.

| Check | Result | How | Admin? |
|---|---|---|---|
| `dotnet build` in working copy | not run | AppContainer, env points `USERPROFILE`/`APPDATA`/`TEMP`/`NUGET_PACKAGES` into the working copy, build servers off | — |
| Read/write outside working copy | not run | only the working copy is ACL'd to the container SID | — |
| Direct network | not run | no `internetClient` capability | — |
| Proxy: loopback TCP + loopback exemption | not run | `CheckNetIsolation LoopbackExempt -a -p=<SID>` | expected yes |
| Proxy: named pipe + in-sandbox forwarder | not run | pipe DACL grants the container SID; forwarder listens on loopback inside | expected no, unproven |
| Memory / process / CPU / time limits | not run | Job Object | expected no |
| Cancel kills everything | not run | `TerminateJobObject` | expected no |

Go/no-go: **open** until the CI run.

## Not yet proven

- Anything on Windows (above).
- Linux on stock ubuntu-latest (AppArmor userns restriction, user manager via linger).
- Isolation between two concurrent sandboxes (SBX-06) was not tested; by construction each command gets its own namespaces and cgroup.

## Fallbacks

- Linux: rootless Podman / Docker with `--network none`, `--memory`, `--pids-limit`, `--cpus`, the working copy bind-mounted, proxy socket mounted. Docker's daemon is root-equivalent; prefer rootless Podman. Costs an image to maintain and slower start.
- Windows: Windows Sandbox / Hyper-V isolated containers (need the feature enabled and admin), or WSL2 + the Linux sandbox.

## Recommendations for S15

1. Linux: implement as prototyped — one transient user scope per command (`systemd-run --user --scope`), bwrap with `--unshare-all --die-with-parent --new-session --clearenv`, kill via `cgroup.kill`. Startup probe = the two calls in `LinuxSandbox.Probe()`; refuse to start with that message if either fails (SBX-07).
2. Keep the proxy host-side and in-process (one per sandbox, its own allow list, decisions logged as events).
3. Add per-toolchain allow-list presets (NuGet incl. CRL/OCSP hosts).
