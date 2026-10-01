using System.Diagnostics;
using System.Globalization;

namespace SandboxSpike;

/// <summary>
/// bubblewrap (user + pid + net + ipc + uts + cgroup namespaces) inside a transient systemd
/// user scope (cgroups v2 limits, delegated to the unprivileged user by systemd).
/// The network namespace is empty except for lo; a socat inside forwards 127.0.0.1:3128 to the
/// host proxy's Unix socket, which is bind-mounted in.
/// </summary>
public sealed class LinuxSandbox : ISandbox
{
    public string WorkDir { get; }
    private readonly string? _proxySocket;
    private readonly string _dotnetRoot;
    public const string InnerSocket = "/run/officina/proxy.sock";

    public LinuxSandbox(string workDir, string? proxySocket, string dotnetRoot)
    {
        WorkDir = workDir; _proxySocket = proxySocket; _dotnetRoot = dotnetRoot;
    }

    public static string? Probe()
    {
        var (c1, o1) = Sh.Run("bwrap", "--unshare-all", "--ro-bind", "/", "/", "true");
        if (c1 != 0) return "bwrap cannot create namespaces: " + o1;
        var (c2, o2) = Sh.Run("systemd-run", "--user", "--scope", "--quiet", "-p", "MemoryMax=64M", "true");
        if (c2 != 0) return "systemd-run --user --scope failed (no delegated cgroup): " + o2;
        return null;
    }

    private List<string> BwrapArgs(IDictionary<string, string> env)
    {
        var a = new List<string> { "--unshare-all", "--die-with-parent", "--new-session", "--clearenv",
            "--ro-bind", "/usr", "/usr" };
        foreach (var d in new[] { "bin", "lib", "lib64", "sbin" })
        {
            var p = "/" + d;
            var fi = new FileInfo(p);
            if (fi.LinkTarget is { } t) a.AddRange(new[] { "--symlink", t, p });
            else if (Directory.Exists(p)) a.AddRange(new[] { "--ro-bind", p, p });
        }
        foreach (var e in new[] { "/etc/ssl", "/etc/ca-certificates", "/etc/alternatives", "/etc/nsswitch.conf", "/etc/localtime" })
            a.AddRange(new[] { "--ro-bind-try", e, e });
        if (!_dotnetRoot.StartsWith("/usr/")) a.AddRange(new[] { "--ro-bind", _dotnetRoot, _dotnetRoot });
        a.AddRange(new[] { "--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp",
            "--bind", WorkDir, WorkDir, "--chdir", WorkDir });
        if (_proxySocket is not null) a.AddRange(new[] { "--bind", _proxySocket, InnerSocket });
        foreach (var (k, v) in env) a.AddRange(new[] { "--setenv", k, v });
        return a;
    }

    public async Task<RunResult> RunShellAsync(string commandLine, Limits limits, CancellationToken ct = default,
        IDictionary<string, string>? extraEnv = null)
    {
        var home = Path.Combine(WorkDir, ".home");
        Directory.CreateDirectory(home);
        var env = new Dictionary<string, string>
        {
            ["PATH"] = $"{_dotnetRoot}:/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = home,
            ["DOTNET_CLI_HOME"] = home,
            ["NUGET_PACKAGES"] = Path.Combine(WorkDir, ".nuget", "packages"),
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
            ["DOTNET_ROOT"] = _dotnetRoot,
            ["TMPDIR"] = "/tmp",
        };
        string prelude = "";
        if (_proxySocket is not null)
        {
            foreach (var k in new[] { "HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy" })
                env[k] = "http://127.0.0.1:3128";
            // Forwarder inside the empty netns: loopback TCP -> bind-mounted Unix socket.
            prelude = $"socat TCP-LISTEN:3128,bind=127.0.0.1,fork,reuseaddr UNIX-CONNECT:{InnerSocket} & " +
                      "i=0; while [ $i -lt 50 ] && ! grep -q ':0C38 ' /proc/net/tcp 2>/dev/null; do sleep 0.05; i=$((i+1)); done; ";
        }
        if (extraEnv is not null) foreach (var (k, v) in extraEnv) env[k] = v;

        var unit = "officina-sbx-" + Guid.NewGuid().ToString("N")[..12];
        var psi = new ProcessStartInfo("systemd-run")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var x in new[] { "--user", "--scope", "--quiet", "--collect", "--unit=" + unit,
                     "-p", "MemoryMax=" + limits.MemoryBytes, "-p", "MemorySwapMax=0", "-p", "OOMPolicy=continue",
                     "-p", "CPUQuota=" + ((int)(limits.CpuCores * 100)).ToString(CultureInfo.InvariantCulture) + "%",
                     "-p", "TasksMax=" + limits.MaxProcesses, "--", "bwrap" })
            psi.ArgumentList.Add(x);
        foreach (var x in BwrapArgs(env)) psi.ArgumentList.Add(x);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("/bin/sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(prelude + commandLine);

        var sink = new OutputSink();
        var facts = new Dictionary<string, string>();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => sink.Add(e.Data);
        p.ErrorDataReceived += (_, e) => sink.Add(e.Data);
        var sw = Stopwatch.StartNew();
        p.Start();
        p.StandardInput.Close();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();

        // Find the scope's cgroup and watch it while the command runs.
        string? cg = null;
        var hostPids = new HashSet<int>();
        using var monitorCts = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            while (!monitorCts.IsCancellationRequested)
            {
                if (cg is null)
                {
                    var (c, o) = Sh.Run("systemctl", "--user", "show", "-p", "ControlGroup", "--value", unit + ".scope");
                    if (c == 0 && o.Length > 1) cg = "/sys/fs/cgroup" + o;
                }
                if (cg is not null && Directory.Exists(cg))
                {
                    void Grab(string f) { try { facts[f] = File.ReadAllText(Path.Combine(cg, f)).Trim().Replace('\n', ' '); } catch { } }
                    Grab("memory.events"); Grab("pids.events"); Grab("cpu.stat"); Grab("memory.peak"); Grab("pids.peak");
                    try { foreach (var l in File.ReadAllLines(Path.Combine(cg, "cgroup.procs"))) hostPids.Add(int.Parse(l)); } catch { }
                }
                try { await Task.Delay(100, monitorCts.Token); } catch { }
            }
        });

        bool timedOut = false, cancelled = false;
        using var timeout = new CancellationTokenSource(limits.WallTime);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try { await p.WaitForExitAsync(linked.Token); }
        catch (OperationCanceledException)
        {
            timedOut = timeout.IsCancellationRequested; cancelled = ct.IsCancellationRequested;
        }
        // Kill-all: whatever is left in the cgroup (background jobs, setsid'd, double-forked).
        KillCgroup(cg, unit);
        await p.WaitForExitAsync();
        monitorCts.Cancel(); await monitor;
        await Task.Delay(200);
        facts["wall_ms"] = sw.ElapsedMilliseconds.ToString();
        facts["host_pids_seen"] = hostPids.Count.ToString();
        facts["host_pids_alive_after"] = hostPids.Count(pid => Directory.Exists("/proc/" + pid)).ToString();
        facts["cgroup_gone_after"] = (cg is null || !Directory.Exists(cg)).ToString();
        return new RunResult(p.ExitCode, sink.ToString(), timedOut, cancelled, facts);
    }

    private static void KillCgroup(string? cg, string unit)
    {
        if (cg is not null && File.Exists(Path.Combine(cg, "cgroup.kill")))
        {
            try { File.WriteAllText(Path.Combine(cg, "cgroup.kill"), "1"); return; } catch { }
        }
        Sh.Run("systemctl", "--user", "kill", "--signal=SIGKILL", unit + ".scope");
    }
}
