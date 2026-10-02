# Spike S00a: Sandbox

These are the spike's findings, kept as a record. **Outcome:** S15 built the sandbox as recommended below, with a
PowerShell forwarder on Windows instead of a self-contained exe, and proved what the spike left open (SBX-06, output caps,
background processes); S21 tested the CPU limit and HTTPS through the proxy on both systems. DESIGN.md §7 describes what
is built.

Prototype: `spikes/sandbox/SandboxSpike`. It is one .NET 10 console app. The OS-specific code is in
`LinuxSandbox.cs` and `WindowsSandbox.cs`. The filtering proxy in `FilterProxy.cs` is shared, and the
misbehaving payloads are in `Payload.cs`. The sample projects are in `spikes/sandbox/sample`.
To run it: `dotnet build spikes/sandbox/SandboxSpike && dotnet spikes/sandbox/SandboxSpike/bin/Debug/net10.0/SandboxSpike.dll --report out.md`.
CI: `.github/workflows/sandbox-spike.yml` runs on ubuntu-latest and windows-latest, on the spike branch only.
Evidence: run [36811807979](https://github.com/sleepyshark85/Officina/actions/runs/36811807979), plus a local run on Linux Mint 22.3 (Ubuntu 24.04 base, kernel 6.17).

## Linux

The checks themselves ran unprivileged on both machines: the local one and ubuntu-latest (Ubuntu 24.04.5, 2 cores).

| Check | Result | How | Root? |
|---|---|---|---|
| `dotnet build` + run in working copy | pass | bwrap `--unshare-all --die-with-parent --new-session --clearenv`. Mounted: `/usr` read-only (it holds the SDK), a few `/etc` files, `/proc`, `/dev`, a tmpfs `/tmp`, and the working copy read-write. `HOME`, `NUGET_PACKAGES` and `DOTNET_CLI_HOME` point into the working copy. | no (see AppArmor below) |
| Read a secret in `~` | pass (blocked) | not mounted, so the path does not exist | no |
| Write outside the working copy | pass (blocked) | same | no |
| Direct network (DNS and raw IP) | pass (blocked) | empty network namespace, only `lo` | no |
| api.nuget.org through the proxy | pass (HTTP 200) | the host proxy listens on a Unix socket, which is bind-mounted in. `socat` inside forwards `127.0.0.1:3128` to it, and `HTTPS_PROXY` is set. | no |
| example.com through the proxy | pass (403) | the proxy checks the CONNECT host against the allow list | no |
| NuGet restore + build through the proxy | pass | a real package reference (Newtonsoft.Json) | no |
| Memory 256 MB | pass (OOM-killed, `oom_kill 1`) | `systemd-run --user --scope -p MemoryMax -p MemorySwapMax=0 -p OOMPolicy=continue` | no* |
| Process count 32 | pass (EAGAIN after 27 children) | `TasksMax`, which sets `pids.max` | no* |
| CPU 0.5 core | pass (0.51 cores used, against 1.99 unlimited on CI and 15.9 locally) | `CPUQuota=50%`, which sets `cpu.max` | no* |
| Wall time 3 s | pass (killed at 3.2 s, 0 processes left) | host timer, then write `1` to the scope's `cgroup.kill` | no |
| Cancel kills everything | pass (11 processes, including `setsid`, double-fork and `nohup`; 0 left) | `cgroup.kill`, with the bwrap pid namespace as a second line of defence | no |

\* These need a systemd **user manager** for the account. On ubuntu-latest and on a desktop one is
already running. A service account needs `loginctl enable-linger <user>` once, as root. systemd then
delegates the `cpu`, `memory` and `pids` controllers to the user, so no root is needed per command.

**Ubuntu AppArmor.** Stock ubuntu-latest has `kernel.apparmor_restrict_unprivileged_userns = 1`, and
bwrap fails with `bwrap: loopback: Failed RTM_NEWADDR: Operation not permitted`. A one-time, root-installed
AppArmor profile for `/usr/bin/bwrap` that grants `userns` fixes it, and all checks then pass
unprivileged (see the workflow's setup step). The other route, turning the sysctl off globally,
weakens the whole machine and is not recommended.

**Linux: go.** The bubblewrap + cgroups v2 design works as written. An installer needs one root
step (the AppArmor profile on Ubuntu ≥ 23.10, and linger for service accounts).

## Windows

The checks ran on windows-latest (Windows 10.0.26100, 2 cores), first as the elevated runner admin
and then **as a freshly created standard user** (`spikeuser`: not in Administrators, Medium
integrity level). Every result was the same in both runs.

| Check | Result | How | Admin? |
|---|---|---|---|
| `dotnet build` + run in working copy | pass | AppContainer profile with zero capabilities. Only the working copy is ACL'd to the container SID. `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`, `TEMP` and `NUGET_PACKAGES` point into it. Build servers are off (`--disable-build-servers`, `UseSharedCompilation=false`, no node reuse). The SDK under Program Files is readable through `ALL APPLICATION PACKAGES`. | no |
| Read a secret in `%USERPROFILE%` | pass (access denied) | the profile is not ACL'd to the container | no |
| Write outside the working copy | pass (access denied) | same | no |
| Direct network | pass (blocked: "forbidden by its access permissions") | no `internetClient` capability | no |
| Proxy on loopback TCP, **without** an exemption | blocked (as expected) | AppContainer loopback isolation | n/a |
| Proxy over a **named pipe** + forwarder inside the sandbox | pass: nuget 200, example.com 403, NuGet restore + build OK | the host proxy listens on `\\.\pipe\officina-proxy-<id>`, whose DACL grants the container SID. A forwarder inside the sandbox listens on `127.0.0.1:3128`; loopback *within* one AppContainer is allowed. | **no** |
| Proxy on loopback TCP + loopback exemption | pass: nuget 200, example.com 403, NuGet restore + build OK | `CheckNetIsolation LoopbackExempt -a -p=<SID>` | **no** (this succeeded as the standard user, for a profile that user owns) |
| …side effect of the exemption | exposed | the sandbox could also connect to an unrelated host-side `127.0.0.1` listener | n/a |
| Memory 256 MB | pass (the allocation fails with OutOfMemory at about 175 MB peak; the process dies) | Job Object `JobMemoryLimit` | no |
| Process count 8 | pass (the 7th child fails with "Not enough quota") | Job Object `ActiveProcessLimit` | no |
| CPU 0.5 core | pass (0.52–0.54 cores, against 1.9 unlimited) | Job Object CPU rate control, hard cap | no |
| Wall time 3 s | pass (3.05 s, 0 processes left) | host timer + `TerminateJobObject` | no |
| Cancel kills everything | pass (7 processes, including `start /b` and a grandchild chain; 0 left) | `TerminateJobObject` (`KILL_ON_JOB_CLOSE` as a backstop) | no |

**Windows: go**, with the named-pipe route for the proxy. Nothing in the Windows sandbox needs admin.

## Risks and things not proven

- **The loopback exemption over-grants.** It opens *all* of host loopback to the sandbox (local
  databases, dev servers, the engine's own ports), not just the proxy. The named-pipe route avoids
  this and needs no exemption, so prefer it. The cost is a small forwarder process inside each sandbox.
- **What the exemption needs is unclear.** As a standard user it worked here, on a fresh local
  account. Enterprise policy (WDAC/GPO) may differ, and the docs suggest it needs admin. The design
  shouldn't depend on it.
- **Windows read exposure is wider than the working copy.** Anything ACL'd to `ALL APPLICATION PACKAGES` is
  readable: Program Files, Windows and parts of the registry. That is fine for toolchains, but only
  user-profile secrets were tested. The Linux sandbox is stricter, because only the mounts listed above exist.
- **The Windows memory limit is a commit limit,** not an OOM kill. Allocations fail and the program
  decides what happens next (dotnet exits). Use a job notification port to report "limit hit".
- **Accounting overshoot.** The Windows job's `ActiveProcesses` counter peaked at 9 with a limit of 8,
  presumably a transient count during creation or exit. The kernel still refused new processes.
- **NuGet contacts extra hosts.** On Linux, NuGet package-signature verification also calls
  `crl3.digicert.com` and `ocsp.digicert.com` over plain HTTP. With them denied the restore still
  succeeded, but per-toolchain allow lists will need such side hosts, or
  `NUGET_CERT_REVOCATION_MODE=offline`. On Windows, no CRL or OCSP request reached the proxy (presumably revocation goes through the OS and is blocked or cached; not investigated).
- **Unix socket path length.** A Unix socket path is limited to 108 bytes, so the proxy socket belongs in `$XDG_RUNTIME_DIR`, not under the working copy.
- **Exit reasons.** The Linux cgroup disappears with its last process. Read `memory.events` and `pids.events` while the command runs to report why it died.
- **Not tested:** isolation between two concurrent sandboxes (SBX-06; it holds by construction:
  separate namespaces and cgroup, and separate AppContainer SID, job and pipe), output size caps
  (SBX-01; the prototype only caps its buffer), and long-running background processes (SBX-03).

## Fallbacks, if a host can't provide this

- Linux: rootless Podman with `--network none`, `--memory`, `--pids-limit` and `--cpus`, the working copy bind-mounted and the proxy socket mounted. Docker works the same way, but its daemon is root-equivalent.
- Windows: Hyper-V isolated containers or Windows Sandbox (feature install and admin needed), or WSL2 running the Linux sandbox.

## Recommendations for S15

1. **Linux:** as prototyped. Create one transient user scope per command, run bwrap inside it, and kill through `cgroup.kill`. The startup probe is `LinuxSandbox.Probe()` (bwrap creates namespaces; `systemd-run --user --scope` works). On failure, refuse to start with that message (SBX-07), and say how to fix it (AppArmor profile, linger).
2. **Windows:** AppContainer per agent and Job Object per command, with the **named-pipe proxy**, not the loopback exemption. Ship the in-sandbox forwarder as a tiny self-contained exe in the working copy's tool directory, to avoid paying for a dotnet start on every command.
3. **Proxy:** one in-process proxy per sandbox, each with its own allow list, and every allow and deny decision emitted as an event. Add per-toolchain allow-list presets.
4. Treat "limit hit" as a first-class result: OOM, pids, CPU throttling and timeout, read from cgroup files on Linux and job notifications on Windows.
