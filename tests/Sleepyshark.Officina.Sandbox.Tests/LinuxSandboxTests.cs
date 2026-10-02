using System.Runtime.Versioning;
using static Sleepyshark.Officina.Sandbox.Tests.RealSandbox;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>
/// The Linux sandbox against the real operating system (TEST-25 on Linux). CI installs bubblewrap and socat and sets up
/// the host as an installer would; elsewhere these tests are skipped.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSandboxTests : IDisposable
{
    private const string LinuxOnly = "The Linux sandbox runs only on Linux.";

    private readonly RealSandbox real = new(new LinuxSandbox());

    public static bool OnLinux => OperatingSystem.IsLinux();

    public void Dispose() => real.Dispose();

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public void The_machine_can_isolate_commands() => Assert.Null(real.Sandbox.Probe());

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Only_the_working_copy_of_the_host_exists_inside()
    {
        var secret = Path.Combine(real.Host, "secret.txt");
        await File.WriteAllTextAsync(secret, "s3cret", Ct);

        var (output, _) = await real.RunAsync($"cat {secret}; echo x > {real.Host}/planted; ls /home; echo built > out.txt; pwd");

        Assert.DoesNotContain("s3cret", output, StringComparison.Ordinal);
        Assert.Contains($"{real.WorkingCopy}\n", output, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(real.Host, "planted")));
        Assert.Equal("built\n", await File.ReadAllTextAsync(Path.Combine(real.WorkingCopy, "out.txt"), Ct));
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Hidden_paths_cannot_be_read_and_read_only_paths_cannot_be_changed()
    {
        var git = Directory.CreateDirectory(Path.Combine(real.WorkingCopy, ".git")).FullName;
        await File.WriteAllTextAsync(Path.Combine(git, "config"), "git-secret", Ct);
        var env = Path.Combine(real.WorkingCopy, ".env");
        await File.WriteAllTextAsync(env, "API_KEY=s3cret", Ct);
        var configuration = Path.Combine(real.WorkingCopy, "sof.json");
        await File.WriteAllTextAsync(configuration, "{}", Ct);

        var (output, exitCode) = await real.RunAsync("cat .env .git/config; ls -A .git; echo '{ \"x\": 1 }' > sof.json", hidden: [git, env], readOnly: [configuration]);

        Assert.NotEqual(0, exitCode);
        Assert.DoesNotContain("s3cret", output, StringComparison.Ordinal);
        Assert.DoesNotContain("git-secret", output, StringComparison.Ordinal);
        Assert.Equal("{}", await File.ReadAllTextAsync(configuration, Ct));
        Assert.Equal("API_KEY=s3cret", await File.ReadAllTextAsync(env, Ct));
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_toolchain_outside_the_system_folders_can_be_run()
    {
        var toolchain = Directory.CreateDirectory(Path.Combine(real.Host, "sdk")).FullName;
        var tool = Path.Combine(toolchain, "sdk-tool");
        await File.WriteAllTextAsync(tool, "#!/bin/sh\necho tool ran\n", Ct);
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var (output, exitCode) = await real.RunAsync("sdk-tool; echo x > " + tool, toolchains: [toolchain]);

        Assert.Contains("tool ran\n", output, StringComparison.Ordinal);
        Assert.NotEqual(0, exitCode);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Commands_see_only_the_environment_they_are_given()
    {
        var (output, _) = await real.RunAsync("env", environment: new Dictionary<string, string> { ["NUGET_TOKEN"] = "t0ken" });

        Assert.Contains("NUGET_TOKEN=t0ken", output, StringComparison.Ordinal);
        Assert.Contains("HOME=/tmp", output, StringComparison.Ordinal);
        Assert.DoesNotContain("XDG_RUNTIME_DIR", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Without_allowed_hosts_there_is_no_network()
    {
        using var server = new Server();

        var (output, exitCode) = await real.RunAsync($"socat -T 2 - TCP:127.0.0.1:{server.Port} </dev/null");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Connection refused", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task The_proxy_lets_through_only_the_allowed_hosts()
    {
        using var server = new Server();
        string Get(string url) => $"printf 'GET {url} HTTP/1.0\\r\\n\\r\\n' | socat -t 5 - TCP:127.0.0.1:3128";

        var (output, _) = await real.RunAsync($"{Get($"http://127.0.0.1:{server.Port}/")}; {Get("http://example.com/")}; socat -T 2 - TCP:127.0.0.1:{server.Port} </dev/null", ["127.0.0.1"]);

        Assert.Contains(Server.Greeting, output, StringComparison.Ordinal);
        Assert.Contains("403 Forbidden", output, StringComparison.Ordinal);
        Assert.Contains("Connection refused", output, StringComparison.Ordinal);
        Assert.Contains($"[Network: allowed 127.0.0.1:{server.Port}.]", output, StringComparison.Ordinal);
        Assert.Contains("[Network: refused example.com, which is not an allowed host.]", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_command_over_its_memory_limit_is_killed()
    {
        var (_, exitCode) = await real.RunAsync("dd if=/dev/zero of=/dev/null bs=200M count=1", limits: Small);

        Assert.Equal(137, exitCode);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_command_cannot_start_more_processes_than_its_limit()
    {
        var (output, exitCode) = await real.RunAsync("i=0; while [ $i -lt 40 ]; do sleep 30 & i=$((i+1)); done; echo all started", limits: Small);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("Cannot fork", output, StringComparison.Ordinal);
        Assert.DoesNotContain("all started", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Output_stops_at_its_limit()
    {
        var (output, exitCode) = await real.RunAsync("yes | head -n 100000", limits: Small);

        Assert.Equal(0, exitCode);
        Assert.InRange(output.Length, 1000, 1100);
        Assert.EndsWith("[Output stopped: the command wrote more than 1000 characters.]\n", output, StringComparison.Ordinal);
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Stopping_a_command_stops_everything_it_started()
    {
        // A new session, a double fork and a plain child: all must end.
        var process = await real.StartAsync("setsid sleep 1234561 & (sleep 1234562 &); sleep 1234563 & echo started; wait");
        Assert.Equal("started", await process.Output.ReadAsync(Ct));

        await process.DisposeAsync();

        Assert.Empty(HostProcesses("sleep 123456"));
    }

    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Sandboxes_cannot_see_or_affect_each_other()
    {
        var other = await real.StartAsync("sleep 9876504 & echo started; wait");
        Assert.Equal("started", await other.Output.ReadAsync(Ct));
        var otherCopy = Directory.CreateTempSubdirectory("officina-copy-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(otherCopy, "work.txt"), "theirs", Ct);

            var (output, _) = await real.RunAsync($"cat {otherCopy}/work.txt; grep -l '987650[4]' /proc/[0-9]*/cmdline; pkill -f '987650[4]'; echo done");

            Assert.Contains("done\n", output, StringComparison.Ordinal);
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
}
