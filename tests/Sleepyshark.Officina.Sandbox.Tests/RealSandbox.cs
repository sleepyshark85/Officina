using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>
/// The sandbox of the operating system the tests run on, with a working copy and a host folder outside it, both real
/// temporary folders. The sandbox is a system boundary, so it is tested against the real operating system (DESIGN.md §11).
/// </summary>
internal sealed class RealSandbox(ISandbox sandbox) : IDisposable
{
    /// <summary>Small limits, so tests can go over them quickly.</summary>
    public static SandboxLimits Small { get; } = new(1, 64 << 20, 16, 1000);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ISandbox Sandbox => sandbox;

    /// <summary>A host folder outside the working copy.</summary>
    public string Host { get; } = Directory.CreateTempSubdirectory("officina-host-").FullName;

    public string WorkingCopy { get; } = Directory.CreateTempSubdirectory("officina-copy-").FullName;

    public void Dispose()
    {
        sandbox.Release(WorkingCopy, []);
        Directory.Delete(Host, recursive: true);
        Directory.Delete(WorkingCopy, recursive: true);
    }

    public Task<ISandboxProcess> StartAsync(string commandLine, string? workingCopy = null, SandboxLimits? limits = null) =>
        sandbox.StartAsync(new(commandLine, workingCopy ?? WorkingCopy, limits ?? SandboxLimits.Default, [], new Dictionary<string, string>(), [], [], []), Ct).AsTask();

    /// <summary>Runs a command to its end, and returns its output, each line ending in <c>\n</c>, and its exit code.</summary>
    public async Task<(string Output, int ExitCode)> RunAsync(
        string commandLine,
        IReadOnlyList<string>? allowedHosts = null,
        SandboxLimits? limits = null,
        Dictionary<string, string>? environment = null,
        IReadOnlyList<string>? hidden = null,
        IReadOnlyList<string>? readOnly = null,
        IReadOnlyList<string>? toolchains = null)
    {
        var command = new SandboxCommand(
            commandLine, WorkingCopy, limits ?? SandboxLimits.Default, allowedHosts ?? [], environment ?? [], hidden ?? [], readOnly ?? [], toolchains ?? []);
        await using var process = await sandbox.StartAsync(command, Ct);
        var output = new StringBuilder();
        await foreach (var line in process.Output.ReadAllAsync(Ct))
        {
            output.Append(line).Append('\n');
        }

        return (output.ToString(), await process.ExitCode);
    }

    /// <summary>
    /// SBX-01: runs work with half a core, and returns how long it took and how much processor time it used, as it measured them
    /// itself: lines <c>WALL &lt;ms&gt;</c> and <c>CPU &lt;ms&gt;</c>, or the shell's <c>times</c> for the processor time. Held to half
    /// a core, work takes at least twice its processor time; other load on the machine can only make it take longer. The test
    /// output reports both.
    /// </summary>
    public async Task<(TimeSpan Wall, TimeSpan Cpu)> TimeWorkAsync(string work)
    {
        var (output, exitCode) = await RunAsync(work, limits: SandboxLimits.Default with { Cpus = 0.5 });
        Assert.True(exitCode == 0, output);
        var wall = Milliseconds(output, "WALL");
        var times = System.Text.RegularExpressions.Regex.Match(output, @"^(\d+)m([\d.]+)s (\d+)m([\d.]+)s$", System.Text.RegularExpressions.RegexOptions.Multiline);
        var cpu = times.Success
            ? TimeSpan.FromMinutes(int.Parse(times.Groups[1].Value, CultureInfo.InvariantCulture) + int.Parse(times.Groups[3].Value, CultureInfo.InvariantCulture))
                + TimeSpan.FromSeconds(double.Parse(times.Groups[2].Value, CultureInfo.InvariantCulture) + double.Parse(times.Groups[4].Value, CultureInfo.InvariantCulture))
            : Milliseconds(output, "CPU");
        TestContext.Current.TestOutputHelper?.WriteLine($"With half a core, the work took {wall.TotalSeconds:0.00} s and used {cpu.TotalSeconds:0.00} s of processor time.");
        return (wall, cpu);

        static TimeSpan Milliseconds(string output, string name) => TimeSpan.FromMilliseconds(long.Parse(
            output.Split('\n').Select(line => line.Trim()).Single(line => line.StartsWith(name + " ", StringComparison.Ordinal))[(name.Length + 1)..], CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// An HTTP server on the host's loopback, which a sandbox can reach only through the proxy. Its reply ends only when it
    /// closes the connection, so the client sees it end only if every hop passes the close on.
    /// </summary>
    public sealed class Server : IDisposable
    {
        public const string Greeting = "hello from the host";

        private readonly TcpListener listener = new(IPAddress.Loopback, 0);

        public Server()
        {
            listener.Start();
            _ = AnswerAsync();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Dispose() => listener.Dispose();

        private async Task AnswerAsync()
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                using var request = new StreamReader(stream, leaveOpen: true);
                await request.ReadLineAsync();
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.0 200 OK\r\n\r\n{Greeting}\n"));
            }
        }
    }
}
