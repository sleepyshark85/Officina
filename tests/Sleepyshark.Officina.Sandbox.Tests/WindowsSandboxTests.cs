using System.Runtime.Versioning;
using static Sleepyshark.Officina.Sandbox.Tests.RealSandbox;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>
/// The Windows sandbox against the real operating system (TEST-25 on Windows). It needs no setup and no admin rights;
/// elsewhere these tests are skipped.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSandboxTests : IDisposable
{
    private const string WindowsOnly = "The Windows sandbox runs only on Windows.";

    // Waiting for a stopped command is bounded, so a failure to stop fails the test instead of hanging it.
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly RealSandbox real = new(new WindowsSandbox());

    public static bool OnWindows => OperatingSystem.IsWindows();

    public void Dispose() => real.Dispose();

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public void The_machine_can_isolate_commands() => Assert.Null(real.Sandbox.Probe());

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Only_the_working_copy_of_the_host_can_be_used()
    {
        await File.WriteAllTextAsync(Path.Combine(real.Host, "secret.txt"), "s3cret", Ct);

        var (output, _) = await real.RunAsync($"type \"{real.Host}\\secret.txt\" & echo x> \"{real.Host}\\planted\" & echo built> out.txt & cd");

        Assert.DoesNotContain("s3cret", output, StringComparison.Ordinal);
        Assert.Contains($"{real.WorkingCopy}\n", output, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(real.Host, "planted")));
        Assert.StartsWith("built", await File.ReadAllTextAsync(Path.Combine(real.WorkingCopy, "out.txt"), Ct), StringComparison.Ordinal);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Hidden_paths_cannot_be_read_and_read_only_paths_cannot_be_changed()
    {
        var git = Directory.CreateDirectory(Path.Combine(real.WorkingCopy, ".git")).FullName;
        await File.WriteAllTextAsync(Path.Combine(git, "config"), "git-secret", Ct);
        var env = Path.Combine(real.WorkingCopy, ".env");
        await File.WriteAllTextAsync(env, "API_KEY=s3cret", Ct);
        var configuration = Path.Combine(real.WorkingCopy, "sof.json");
        await File.WriteAllTextAsync(configuration, "{}", Ct);

        var (output, _) = await real.RunAsync("type .env & type .git\\config & echo {\"x\": 1}> sof.json & del /f sof.json & type sof.json", hidden: [git, env], readOnly: [configuration]);

        Assert.DoesNotContain("s3cret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("git-secret", output, StringComparison.Ordinal);
        Assert.Contains("{}", output, StringComparison.Ordinal);
        Assert.Equal("{}", await File.ReadAllTextAsync(configuration, Ct));
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task A_toolchain_outside_the_system_folders_can_be_run()
    {
        var toolchain = Directory.CreateDirectory(Path.Combine(real.Host, "sdk")).FullName;
        var tool = Path.Combine(toolchain, "sdk-tool.cmd");
        await File.WriteAllTextAsync(tool, "@echo tool ran", Ct);

        var (output, _) = await real.RunAsync($"sdk-tool & echo x> \"{tool}\"", toolchains: [toolchain]);

        Assert.Contains("tool ran\n", output, StringComparison.Ordinal);
        Assert.Equal("@echo tool ran", await File.ReadAllTextAsync(tool, Ct));
    }

    // S19: what the container leaves outside the working copy goes when the working copy does.
    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Releasing_a_working_copy_removes_its_home_folder_and_its_rights_on_the_toolchains()
    {
        var toolchain = Directory.CreateDirectory(Path.Combine(real.Host, "sdk")).FullName;
        await File.WriteAllTextAsync(Path.Combine(toolchain, "sdk-tool.cmd"), "@echo tool ran", Ct);
        var (output, _) = await real.RunAsync("sdk-tool & echo %USERPROFILE%", toolchains: [toolchain]);
        var home = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
        Assert.True(Directory.Exists(home));
        Assert.NotEmpty(Containers(toolchain));

        real.Sandbox.Release(real.WorkingCopy, [toolchain]);

        Assert.False(Directory.Exists(home));
        Assert.Empty(Containers(toolchain));
        real.Sandbox.Release(real.WorkingCopy, [toolchain]); // nothing left to remove is not an error
        real.Sandbox.Release(Path.Combine(real.Host, "never-ran"), [toolchain]); // nor is a working copy that never ran a command
    }

    // AppContainer SIDs all begin S-1-15-2-.
    private static List<System.Security.AccessControl.AuthorizationRule> Containers(string folder) =>
        [.. new DirectoryInfo(folder).GetAccessControl().GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.AuthorizationRule>().Where(rule => rule.IdentityReference.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal))];

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Commands_see_only_the_environment_they_are_given()
    {
        var (output, _) = await real.RunAsync("set", environment: new Dictionary<string, string> { ["NUGET_TOKEN"] = "t0ken" });

        Assert.Contains("NUGET_TOKEN=t0ken", output, StringComparison.Ordinal);
        Assert.DoesNotContain("USERNAME=", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Without_allowed_hosts_there_is_no_network()
    {
        using var server = new Server();

        var (output, exitCode) = await real.RunAsync($"curl -s -m 5 http://127.0.0.1:{server.Port}/");

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain(Server.Greeting, output, StringComparison.Ordinal);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task The_proxy_lets_through_only_the_allowed_hosts()
    {
        using var server = new Server();

        var (output, _) = await real.RunAsync(
            $"curl -s http://127.0.0.1:{server.Port}/ & curl -s -i http://example.com/ & curl -s -m 5 --noproxy * http://127.0.0.1:{server.Port}/",
            ["127.0.0.1"]);

        Assert.Single(output.Split('\n'), line => line == Server.Greeting);
        Assert.Contains("403 Forbidden", output, StringComparison.Ordinal);
        Assert.Contains("[Network: refused example.com, which is not an allowed host.]", output, StringComparison.Ordinal);
    }

    // HTTPS goes through the proxy as a CONNECT tunnel, which carries whatever the client sends; the proxy checks only its host.
    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task The_proxy_tunnels_connections_to_allowed_hosts_only()
    {
        using var server = new Server();

        var (output, _) = await real.RunAsync($"curl -sS -m 10 -p http://127.0.0.1:{server.Port}/ & curl -sS -m 10 -p http://example.com/", ["127.0.0.1"]);

        Assert.Contains(Server.Greeting, output, StringComparison.Ordinal);
        Assert.Contains("403", output, StringComparison.Ordinal);
        Assert.Contains("[Network: refused example.com, which is not an allowed host.]", output, StringComparison.Ordinal);
    }

    // SBX-01: the processor limit holds, as a hard cap.
    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task A_command_gets_no_more_processor_time_than_its_limit()
    {
        var (two, half) = await real.TimeWorkAsync("for /l %i in (1,1,3000000) do @rem");

        Assert.True(half >= two * 1.5, $"Half a core took {half}, two cores {two}.");
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task A_command_over_its_memory_limit_fails()
    {
        // The limit is on committed memory: the allocation fails, and the program ends.
        var (output, exitCode) = await real.RunAsync(
            "powershell -NoProfile -NonInteractive -Command \"$ErrorActionPreference = 'Stop'; $a = [byte[]]::new(1GB); 'allocated'\"", limits: Small with { MemoryBytes = 256 << 20 });

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("allocated\n", output, StringComparison.Ordinal);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task A_command_cannot_start_more_processes_than_its_limit()
    {
        await using var process = await real.StartAsync(
            "(for /l %i in (1,1,30) do @start \"\" /b cmd /d /c \"for /l %j in (0,0,1) do @rem\" || echo refused) & echo done", limits: Small);

        // The children run until the command is stopped, so reading stops at the first refusal, or at the end of the loop.
        var lines = new List<string>();
        await foreach (var line in process.Output.ReadAllAsync(Ct))
        {
            lines.Add(line.Trim());
            if (line.Trim() is "refused" or "done")
            {
                break;
            }
        }

        Assert.Equal("refused", lines[^1]);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Output_stops_at_its_limit()
    {
        var (output, exitCode) = await real.RunAsync("for /l %i in (1,1,2000) do @echo yyyyyyyy", limits: Small);

        Assert.Equal(0, exitCode);
        Assert.InRange(output.Length, 1000, 1100);
        Assert.EndsWith("[Output stopped: the command wrote more than 1000 characters.]\n", output, StringComparison.Ordinal);
    }

    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Stopping_a_command_stops_everything_it_started()
    {
        // The command itself ends at once; the child it started holds the output open until it ends too.
        var process = await real.StartAsync("start \"\" /b cmd /d /c \"for /l %j in (0,0,1) do @rem\" & echo started");
        Assert.Equal("started", await process.Output.ReadAsync(Ct));
        Assert.False(process.ExitCode.IsCompleted);

        await process.DisposeAsync().AsTask().WaitAsync(Bound, Ct);

        Assert.True(process.ExitCode.IsCompleted);
    }

    // SBX-06.
    [Fact(Skip = WindowsOnly, SkipUnless = nameof(OnWindows))]
    public async Task Sandboxes_cannot_see_or_affect_each_other()
    {
        var otherCopy = Directory.CreateTempSubdirectory("officina-copy-").FullName;
        await File.WriteAllTextAsync(Path.Combine(otherCopy, "work.txt"), "theirs", Ct);
        var other = await real.StartAsync("start \"\" /b cmd /d /c \"for /l %j in (0,0,1) do @rem\" & echo started", otherCopy);
        try
        {
            Assert.Equal("started", await other.Output.ReadAsync(Ct));

            var (output, _) = await real.RunAsync($"type \"{otherCopy}\\work.txt\" & echo done");

            Assert.EndsWith("done\n", output, StringComparison.Ordinal);
            Assert.DoesNotContain("theirs", output, StringComparison.Ordinal);
            Assert.False(other.ExitCode.IsCompleted);
        }
        finally
        {
            await other.DisposeAsync().AsTask().WaitAsync(Bound, Ct);
            Directory.Delete(otherCopy, recursive: true);
        }
    }
}
