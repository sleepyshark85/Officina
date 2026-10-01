using System.Diagnostics;
using System.Text;

namespace SandboxSpike;

/// <summary>Limits for one command. CpuCores is a fraction of one core (0.5 = half a core).</summary>
public sealed record Limits(double CpuCores, long MemoryBytes, int MaxProcesses, TimeSpan WallTime)
{
    public static Limits Default => new(2.0, 1L << 30, 256, TimeSpan.FromMinutes(5));
}

public sealed record RunResult(int ExitCode, string Output, bool TimedOut, bool Cancelled, IReadOnlyDictionary<string, string> Facts);

public interface ISandbox
{
    string WorkDir { get; }
    /// <summary>Run a shell command line (sh -c on Linux, cmd /c on Windows) inside the sandbox.</summary>
    Task<RunResult> RunShellAsync(string commandLine, Limits limits, CancellationToken ct = default,
        IDictionary<string, string>? extraEnv = null);
}

/// <summary>Collects stdout+stderr into one buffer with a cap (SBX-01 output size).</summary>
public sealed class OutputSink
{
    private readonly StringBuilder _sb = new();
    private readonly int _cap;
    public OutputSink(int cap = 256 * 1024) => _cap = cap;
    public void Add(string? line)
    {
        if (line is null) return;
        lock (_sb) { if (_sb.Length < _cap) _sb.AppendLine(line); }
    }
    public override string ToString() { lock (_sb) return _sb.ToString(); }
}

public static class Sh
{
    /// <summary>Run a host-side helper process (not sandboxed) and return (exit, output).</summary>
    public static (int, string) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return (p.ExitCode, (o.Result + e.Result).Trim());
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }
}
