using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SandboxSpike;

if (args.Length > 0 && args[0] == "payload") return Payload.Run(args[1..]);

string? reportPath = null;
for (int i = 0; i < args.Length; i++) if (args[i] == "--report") reportPath = args[++i];

var results = new List<(string Check, string Status, string How, string Detail)>();
void Record(string check, string status, string how, string detail)
{
    detail = detail.ReplaceLineEndings(" ").Replace("|", "/");
    if (detail.Length > 400) detail = detail[..400] + "…";
    results.Add((check, status, how, detail));
    Console.WriteLine($"[{status}] {check} — {detail}");
}
string Tail(string s, int n = 300) => s.Length <= n ? s.Trim() : "…" + s[^n..].Trim();

bool isWin = OperatingSystem.IsWindows();
var id = Guid.NewGuid().ToString("N")[..8];
var spikeDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var sampleDir = Path.Combine(spikeDir, "sample");
var root = isWin ? $@"C:\officina-spike\{id}" : Path.Combine(Environment.GetEnvironmentVariable("SPIKE_ROOT") ?? Path.GetTempPath(), "officina-spike-" + id);
var wc = Path.Combine(root, "wc");
Directory.CreateDirectory(root);
var realHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var secret = Path.Combine(realHome, "officina-spike-secret.txt");
File.WriteAllText(secret, "TOPSECRET-" + id);
var outsideWrite = Path.Combine(realHome, "officina-spike-written-" + id + ".txt");

Console.WriteLine($"OS={RuntimeInformation.OSDescription} cores={Environment.ProcessorCount} wc={wc} admin/root={IsElevated()}");

await using var proxy = new FilterProxy(new[] { "api.nuget.org" });
ISandbox sb;
string? cnaName = null;
WindowsSandbox? wsb = null;
int loopbackPort = 0;
string pipeName = "officina-proxy-" + id;

if (isWin)
{
    cnaName = "officina.spike." + id;
    wsb = new WindowsSandbox(wc, cnaName); // grants the AppContainer SID on wc before anything is copied in
    sb = wsb;
    loopbackPort = proxy.ListenTcpLoopback();
    var ps = new PipeSecurity();
    ps.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
    ps.AddAccessRule(new PipeAccessRule(wsb.Sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
    proxy.ListenNamedPipe(() => NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps));
    Console.WriteLine($"AppContainer SID={wsb.Sid} proxy=127.0.0.1:{loopbackPort} pipe={pipeName}");
}
else
{
    var probe = LinuxSandbox.Probe();
    if (probe is not null)
    {
        Record("probe", "FAIL", "bwrap + systemd-run --user", probe);
        await WriteReport();
        return 1;
    }
    Record("probe", "PASS", "bwrap --unshare-all; systemd-run --user --scope", "user namespaces and delegated cgroup available");
    // sun_path is limited to 108 bytes, so the socket can't live under a deep working dir.
    var sock = Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp", $"officina-proxy-{id}.sock");
    proxy.ListenUnix(sock);
    var dotnetRoot = Path.GetDirectoryName(Path.GetFullPath(new FileInfo(Environment.ProcessPath!).ResolveLinkTarget(true)?.FullName ?? Environment.ProcessPath!))!;
    sb = new LinuxSandbox(wc, sock, dotnetRoot);
    Directory.CreateDirectory(wc);
}

CopyDir(AppContext.BaseDirectory, Path.Combine(wc, ".payload"));
CopyDir(sampleDir, wc);
var P = $"dotnet \"{Path.Combine(wc, ".payload", "SandboxSpike.dll")}\" payload";
var std = Limits.Default;

// 1. dotnet build inside the working copy
{
    var r = await sb.RunShellAsync($"dotnet build Hello{Sep()}Hello.csproj --disable-build-servers -m:1 -p:UseSharedCompilation=false -v:q -nologo && dotnet Hello{Sep()}bin{Sep()}Debug{Sep()}net10.0{Sep()}Hello.dll", std);
    Record("dotnet build in working copy", r.ExitCode == 0 && r.Output.Contains("hello from the sandbox") ? "PASS" : "FAIL",
        "build + run sample/Hello inside sandbox", $"exit={r.ExitCode} {Tail(r.Output)}");
}
// 2. read outside the working copy
{
    var r = await sb.RunShellAsync($"{P} readfile \"{secret}\"", std);
    Record("read secret outside working copy blocked", r.Output.Contains("READ_FAILED") && !r.Output.Contains("TOPSECRET") ? "PASS" : "FAIL",
        isWin ? "AppContainer: only wc is ACL'd to the container SID" : "bwrap: home dir not mounted", Tail(r.Output));
}
{
    var r = await sb.RunShellAsync($"{P} writefile \"{outsideWrite}\"", std);
    Record("write outside working copy blocked", !File.Exists(outsideWrite) ? "PASS" : "FAIL", "same as above", Tail(r.Output));
}
// 3. network
{
    var r = await sb.RunShellAsync($"{P} connect example.com 443 {(isWin ? "&" : ";")} {P} connect 1.1.1.1 443", std);
    Record("direct network (no proxy) blocked", !System.Text.RegularExpressions.Regex.IsMatch(r.Output, "^CONNECTED", System.Text.RegularExpressions.RegexOptions.Multiline) ? "PASS" : "FAIL",
        isWin ? "AppContainer with zero capabilities (no internetClient)" : "empty network namespace (lo only)", Tail(r.Output));
}

if (!isWin)
{
    // LinuxSandbox always sets HTTPS_PROXY to the in-sandbox socat forwarder.
    await NetChecks("Unix socket + socat in sandbox", null);
}
else
{
    var exemptedBefore = Sh.Run("CheckNetIsolation.exe", "LoopbackExempt", "-s").Item2.Contains(wsb!.Sid.ToString(), StringComparison.OrdinalIgnoreCase);
    // Variant A without exemption.
    var envA = Env("HTTPS_PROXY", $"http://127.0.0.1:{loopbackPort}");
    {
        var r = await sb.RunShellAsync($"{P} fetch https://api.nuget.org/v3/index.json", std, default, envA);
        Record("loopback proxy WITHOUT loopback exemption", r.Output.Contains("HTTP 200") ? "INFO-reachable" : "INFO-blocked",
            "AppContainer -> 127.0.0.1 TCP proxy, no exemption", $"exempted_before={exemptedBefore} {Tail(r.Output)}");
    }
    // Variant B: named pipe (DACL grants container SID) + forwarder inside the sandbox on its own loopback.
    var envB = Env("HTTPS_PROXY", "http://127.0.0.1:3128");
    {
        var ready = Path.Combine(wc, "fwd.ready");
        var r = await sb.RunShellAsync($"start \"\" /b {P} forward 3128 {pipeName} \"{ready}\" & {P} fetch https://api.nuget.org/v3/index.json \"{ready}\"", std, default, envB);
        Record("named-pipe proxy + in-sandbox forwarder (no admin)", r.Output.Contains("HTTP 200") ? "PASS" : "FAIL",
            "proxy on \\\\.\\pipe\\ (DACL: container SID); forwarder in sandbox listens 127.0.0.1:3128", Tail(r.Output));
        if (r.Output.Contains("HTTP 200"))
            await NetChecks("named pipe + in-sandbox forwarder", envB, $"start \"\" /b {P} forward 3128 {pipeName} \"{ready}\" & ", ready);
    }
    // Variant A with the loopback exemption (needs admin).
    var (ec, eo) = Sh.Run("CheckNetIsolation.exe", "LoopbackExempt", "-a", "-p=" + wsb.Sid);
    Record("add loopback exemption", ec == 0 ? "PASS" : "FAIL", "CheckNetIsolation LoopbackExempt -a -p=<SID>", $"exit={ec} elevated={IsElevated()} {Tail(eo)}");
    if (ec == 0)
    {
        await NetChecks("loopback TCP proxy + exemption", envA);
        // Risk: exemption opens *all* of loopback, not just the proxy.
        var other = new TcpListener(IPAddress.Loopback, 0); other.Start();
        var op = ((IPEndPoint)other.LocalEndpoint).Port;
        var r = await sb.RunShellAsync($"{P} connect 127.0.0.1 {op}", std);
        Record("exemption also exposes other loopback services", r.Output.StartsWith("CONNECTED") ? "INFO-exposed" : "INFO-not-exposed",
            "connect to an unrelated host-side 127.0.0.1 listener", Tail(r.Output));
        other.Stop();
        Sh.Run("CheckNetIsolation.exe", "LoopbackExempt", "-d", "-p=" + wsb.Sid);
    }
}

// 4. limits
{
    var r = await sb.RunShellAsync($"{P} membomb" + (isWin ? "" : "; echo PAYLOAD_EXIT=$?; sleep 1"), std with { MemoryBytes = 256L << 20, WallTime = TimeSpan.FromSeconds(60) });
    var last = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.StartsWith("allocated")) ?? "none";
    var mb = int.TryParse(last.Split(' ').ElementAtOrDefault(1), out var m) ? m : 0;
    Record("memory limit (256 MB) stops runaway", (r.ExitCode != 0 || r.Output.Contains("PAYLOAD_EXIT=137")) && !r.TimedOut && mb < 320 ? "PASS" : "FAIL",
        isWin ? "Job Object JobMemoryLimit" : "cgroup memory.max + memory.swap.max=0",
        $"exit={r.ExitCode} last='{last}' {Facts(r, "memory.events", "memory.peak", "peak_job_memory_mb")} {Tail(r.Output, 150)}");
}
{
    RunResult r;
    if (isWin) r = await sb.RunShellAsync($"{P} spawn 40", std with { MaxProcesses = 8, WallTime = TimeSpan.FromSeconds(60) });
    else r = await sb.RunShellAsync("python3 -c \"import os,time\nn=0\ntry:\n  for i in range(300):\n    if os.fork()==0: time.sleep(30); os._exit(0)\n    n+=1\nexcept OSError as e: print('FORK_FAILED', e)\nprint('SPAWNED=%d of 300' % n)\ntime.sleep(1)\"", std with { MaxProcesses = 32, WallTime = TimeSpan.FromSeconds(20) });
    bool ok = isWin
        ? r.Output.Contains("SPAWNED=") && !r.Output.Contains("SPAWNED=40") && r.Output.Contains("quota")
        : r.Output.Contains("FORK_FAILED") && System.Text.RegularExpressions.Regex.Match(r.Output, @"SPAWNED=(\d+)") is { Success: true } mm && int.Parse(mm.Groups[1].Value) < 32;
    Record("process-count limit stops fork bomb", ok ? "PASS" : "FAIL",
        isWin ? "Job Object ActiveProcessLimit=8" : "cgroup pids.max=32 (TasksMax)",
        $"{Facts(r, "pids.events", "pids.peak", "peak_active_processes", "active_processes_after_kill")} {Tail(r.Output, 250)}");
}
{
    int threads = Math.Max(2, Environment.ProcessorCount);
    var baseline = await sb.RunShellAsync($"{P} spin {threads} 4", std with { CpuCores = Environment.ProcessorCount });
    var r = await sb.RunShellAsync($"{P} spin {threads} 4", std with { CpuCores = 0.5 });
    double Cores(RunResult x) { var l = x.Output.Split('\n').FirstOrDefault(s => s.StartsWith("CORES_USED=")); return l is null ? -1 : double.Parse(l[11..].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture); }
    var c = Cores(r);
    Record("CPU limit (0.5 core) throttles spinner", c >= 0 && c <= 0.7 ? "PASS" : "FAIL",
        isWin ? "Job Object CPU rate hard cap" : "cgroup cpu.max (CPUQuota=50%)",
        $"limited={c:F2} cores, unlimited baseline={Cores(baseline):F2} cores ({threads} threads) {Facts(r, "cpu.stat")}");
}
{
    var r = await sb.RunShellAsync($"{P} sleep 600", std with { WallTime = TimeSpan.FromSeconds(3) });
    var wall = long.Parse(r.Facts["wall_ms"]);
    bool clean = isWin ? r.Facts["active_processes_after_kill"] == "0" : r.Facts["host_pids_alive_after"] == "0";
    Record("wall-time limit (3 s) kills command", r.TimedOut && wall < 8000 && clean ? "PASS" : "FAIL",
        isWin ? "host timer + TerminateJobObject" : "host timer + cgroup.kill", $"wall={wall}ms {Facts(r, "host_pids_alive_after", "active_processes_after_kill")}");
}
// 5. kill everything on cancel
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var cmd = isWin
        ? $"start \"\" /b {P} sleep 600 & {P} tree 3"
        : $"setsid sleep 600 & (sleep 600 &) ; nohup sleep 600 >/dev/null 2>&1 & {P} tree 3";
    var r = await sb.RunShellAsync(cmd, std, cts.Token);
    bool ok = r.Cancelled && (isWin
        ? r.Facts["active_processes_after_kill"] == "0" && int.Parse(r.Facts["active_processes_before_kill"]) >= 5
        : r.Facts["host_pids_alive_after"] == "0" && int.Parse(r.Facts["host_pids_seen"]) >= 5);
    Record("cancel kills whole process tree", ok ? "PASS" : "FAIL",
        isWin ? "TerminateJobObject (+KILL_ON_JOB_CLOSE)" : "cgroup.kill on the scope (setsid/double-fork/nohup can't escape)",
        Facts(r, "host_pids_seen", "host_pids_alive_after", "active_processes_before_kill", "active_processes_after_kill", "cgroup_gone_after"));
}

File.Delete(secret);
if (cnaName is not null) WindowsSandbox.DeleteProfile(cnaName);
await WriteReport();
return results.Any(r => r.Status == "FAIL") ? 1 : 0;

// ---------- helpers ----------
async Task NetChecks(string variant, Dictionary<string, string>? env, string prefix = "", string? waitFile = null)
{
    var w = waitFile is null ? "" : $" \"{waitFile}\"";
    var before = proxy.Log.Count;
    var r1 = await sb.RunShellAsync($"{prefix}{P} fetch https://api.nuget.org/v3/index.json{w}", std, default, env);
    Record($"allowed host via proxy [{variant}]", r1.Output.Contains("HTTP 200") ? "PASS" : "FAIL", "GET https://api.nuget.org/v3/index.json", Tail(r1.Output));
    var r2 = await sb.RunShellAsync($"{prefix}{P} fetch https://example.com/{w}", std, default, env);
    Record($"other host via proxy denied [{variant}]", !r2.Output.Contains("HTTP 200") && proxy.Log.Any(l => l.StartsWith("DENY example.com")) ? "PASS" : "FAIL",
        "GET https://example.com/ -> proxy 403", Tail(r2.Output));
    var r3 = await sb.RunShellAsync($"{prefix}dotnet build WithPackage{Sep()}WithPackage.csproj --disable-build-servers -m:1 -p:UseSharedCompilation=false -v:q -nologo && dotnet WithPackage{Sep()}bin{Sep()}Debug{Sep()}net10.0{Sep()}WithPackage.dll", std with { WallTime = TimeSpan.FromMinutes(5) }, default, env);
    var log = string.Join(", ", proxy.Log.Skip(before).Distinct());
    Record($"NuGet restore + build through proxy [{variant}]", r3.ExitCode == 0 && r3.Output.Contains("\"ok\":true") ? "PASS" : "FAIL",
        "dotnet build sample/WithPackage (Newtonsoft.Json)", $"exit={r3.ExitCode} proxy log: {log} {Tail(r3.Output, 200)}");
}

static Dictionary<string, string> Env(params string[] kv)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i < kv.Length; i += 2) { d[kv[i]] = kv[i + 1]; d[kv[i].ToLowerInvariant()] = kv[i + 1]; }
    d["HTTP_PROXY"] = d["HTTPS_PROXY"];
    return d;
}

string Sep() => isWin ? "\\" : "/";

static string Facts(RunResult r, params string[] keys) =>
    string.Join(" ", keys.Where(r.Facts.ContainsKey).Select(k => $"{k}=[{r.Facts[k]}]"));

static void CopyDir(string from, string to)
{
    foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(from, f);
        if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")) continue;
        var dst = Path.Combine(to, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(f, dst, true);
    }
}

static bool IsElevated()
{
    if (OperatingSystem.IsWindows()) return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    return Environment.UserName == "root";
}

async Task WriteReport()
{
    var sbr = new StringBuilder();
    sbr.AppendLine($"### Sandbox spike — {RuntimeInformation.OSDescription} (elevated: {IsElevated()})\n");
    sbr.AppendLine("| Check | Result | How | Detail |\n|---|---|---|---|");
    foreach (var (c, s, h, d) in results) sbr.AppendLine($"| {c} | {s} | {h} | {d} |");
    Console.WriteLine(sbr);
    if (reportPath is not null) await File.WriteAllTextAsync(reportPath, sbr.ToString());
}
