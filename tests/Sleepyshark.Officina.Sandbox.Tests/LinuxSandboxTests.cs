using System.Net;
using System.Net.Sockets;
using System.Text;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>
/// The Linux sandbox against the real operating system (TEST-25 on Linux). CI installs bubblewrap and socat and sets up
/// the host as an installer would; elsewhere these tests are skipped.
/// </summary>
public sealed class LinuxSandboxTests : IDisposable
{
    private const string LinuxOnly = "The Linux sandbox runs only on Linux.";

    private readonly LinuxSandbox sandbox = new();
    private readonly string host = Directory.CreateTempSubdirectory("officina-host-").FullName;
    private readonly string workingCopy = Directory.CreateTempSubdirectory("officina-copy-").FullName;

    public static bool OnLinux => OperatingSystem.IsLinux();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(host, recursive: true);
        Directory.Delete(workingCopy, recursive: true);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public void The_machine_can_isolate_commands() => Assert.Null(sandbox.Probe());

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Only_the_working_copy_of_the_host_exists_inside()
    {
        var secret = Path.Combine(host, "secret.txt");
        await File.WriteAllTextAsync(secret, "s3cret", Ct);

        var (output, _) = await RunAsync($"cat {secret}; echo x > {host}/planted; ls /home; echo built > out.txt; pwd");

        Assert.DoesNotContain("s3cret", output, StringComparison.Ordinal);
        Assert.Contains($"{workingCopy}\n", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(host, "planted")));
        Assert.Equal("built\n", await File.ReadAllTextAsync(Path.Combine(workingCopy, "out.txt"), Ct));
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Commands_see_only_the_environment_they_are_given()
    {
        var (output, _) = await RunAsync("env", environment: new Dictionary<string, string> { ["NUGET_TOKEN"] = "t0ken" });

        Assert.Contains("NUGET_TOKEN=t0ken", output, StringComparison.Ordinal);
        Assert.Contains("HOME=/tmp", output, StringComparison.Ordinal);
        Assert.DoesNotContain("XDG_RUNTIME_DIR", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Without_allowed_hosts_there_is_no_network()
    {
        using var server = new Server();

        var (output, exitCode) = await RunAsync($"socat -T 2 - TCP:127.0.0.1:{server.Port} </dev/null");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Connection refused", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task The_proxy_lets_through_only_the_allowed_hosts()
    {
        using var server = new Server();
        string Get(string url) => $"printf 'GET {url} HTTP/1.0\\r\\n\\r\\n' | socat -t 5 - TCP:127.0.0.1:3128";

        var (output, _) = await RunAsync($"{Get($"http://127.0.0.1:{server.Port}/")}; {Get("http://example.com/")}; socat -T 2 - TCP:127.0.0.1:{server.Port} </dev/null", ["127.0.0.1"]);

        Assert.Contains("hello from the host", output, StringComparison.Ordinal);
        Assert.Contains("403 Forbidden", output, StringComparison.Ordinal);
        Assert.Contains("Connection refused", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_command_over_its_memory_limit_is_killed()
    {
        var (_, exitCode) = await RunAsync("dd if=/dev/zero of=/dev/null bs=200M count=1", limits: Small);

        Assert.Equal(137, exitCode);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_command_cannot_start_more_processes_than_its_limit()
    {
        var (output, exitCode) = await RunAsync("i=0; while [ $i -lt 40 ]; do sleep 30 & i=$((i+1)); done; echo all started", limits: Small);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Cannot fork", output, StringComparison.Ordinal);
        Assert.DoesNotContain("all started", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Output_stops_at_its_limit()
    {
        var (output, exitCode) = await RunAsync("yes | head -n 100000", limits: Small);

        Assert.Equal(0, exitCode);
        Assert.InRange(output.Length, 1000, 1100);
        Assert.EndsWith("[Output stopped: the command wrote more than 1000 characters.]\n", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Stopping_a_command_stops_everything_it_started()
    {
        // A new session, a double fork and a plain child: all must end.
        var process = await StartAsync("setsid sleep 1234561 & (sleep 1234562 &); sleep 1234563 & echo started; wait");
        Assert.Equal("started", await process.Output.ReadAsync(Ct));

        await process.DisposeAsync();

        Assert.Empty(HostProcesses("sleep 123456"));
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Sandboxes_cannot_see_or_affect_each_other()
    {
        var other = await StartAsync("sleep 9876504 & echo started; wait");
        Assert.Equal("started", await other.Output.ReadAsync(Ct));
        var otherCopy = Directory.CreateTempSubdirectory("officina-copy-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(otherCopy, "work.txt"), "theirs", Ct);

            var (output, _) = await RunAsync($"cat {otherCopy}/work.txt; grep -l '987650[4]' /proc/[0-9]*/cmdline; pkill -f '987650[4]'; echo done");

            Assert.Equal("done", output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1]);
            Assert.DoesNotContain("theirs", output, StringComparison.Ordinal);
            Assert.DoesNotContain("/cmdline", output, StringComparison.Ordinal);
            Assert.False(other.ExitCode.IsCompleted);
        }
        finally
        {
            await other.DisposeAsync();
            Directory.Delete(otherCopy, recursive: true);
        }
    }

    private static SandboxLimits Small { get; } = new(1, 64 << 20, 16, 1000);

    private Task<ISandboxProcess> StartAsync(string commandLine) =>
        sandbox.StartAsync(new(commandLine, workingCopy, SandboxLimits.Default, [], new Dictionary<string, string>()), Ct).AsTask();

    private async Task<(string Output, int ExitCode)> RunAsync(
        string commandLine, IReadOnlyList<string>? allowedHosts = null, SandboxLimits? limits = null, Dictionary<string, string>? environment = null)
    {
        await using var process = await sandbox.StartAsync(
            new(commandLine, workingCopy, limits ?? SandboxLimits.Default, allowedHosts ?? [], environment ?? []), Ct);
        var output = new StringBuilder();
        await foreach (var line in process.Output.ReadAllAsync(Ct))
        {
            output.Append(line).Append('\n');
        }

        return (output.ToString(), await process.ExitCode);
    }

    /// <summary>The host's processes whose command line contains the text.</summary>
    private static List<string> HostProcesses(string text) =>
        [.. Directory.EnumerateDirectories("/proc").Where(path => int.TryParse(Path.GetFileName(path), out _)).Where(path =>
        {
            try
            {
                return File.ReadAllText(Path.Combine(path, "cmdline")).Contains(text, StringComparison.Ordinal);
            }
            catch (IOException)
            {
                return false;
            }
        })];

    /// <summary>An HTTP server on the host's loopback, which a sandbox can reach only through the proxy.</summary>
    private sealed class Server : IDisposable
    {
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
                await stream.WriteAsync("HTTP/1.0 200 OK\r\n\r\nhello from the host\n"u8.ToArray());
            }
        }
    }
}
