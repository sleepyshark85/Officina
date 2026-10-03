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

    // The working copies' home folders go here, not in the user's own data folder.
    private readonly string homes = Directory.CreateTempSubdirectory("officina-homes-").FullName;
    private readonly RealSandbox real;

    public LinuxSandboxTests() => real = new(new LinuxSandbox(homes));

    public static bool OnLinux => OperatingSystem.IsLinux();

    public void Dispose()
    {
        real.Dispose();
        Directory.Delete(homes, recursive: true);
    }

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
        Assert.Contains("DOTNET_NOLOGO=1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("XDG_RUNTIME_DIR", output, StringComparison.Ordinal);
    }

    // A working copy's home folder keeps what its commands cache, such as NuGet's packages, until the working copy is released.
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_working_copys_home_folder_lasts_until_it_is_released()
    {
        var (home, _) = await real.RunAsync("echo kept > ~/cache; echo $HOME");
        home = home.Trim();
        var (kept, _) = await real.RunAsync("cat ~/cache");
        using var otherCopy = new RealSandbox(real.Sandbox);
        var (other, _) = await otherCopy.RunAsync("cat ~/cache; echo $HOME");

        real.Sandbox.Release(real.WorkingCopy, []);

        Assert.Equal("kept\n", kept);
        Assert.DoesNotContain("kept", other, StringComparison.Ordinal);
        Assert.DoesNotContain(home, other, StringComparison.Ordinal);
        Assert.False(Directory.Exists(home));
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(homes).Count()); // the other copy's home and the file that names its copy
        real.Sandbox.Release(real.WorkingCopy, []); // nothing left to remove is not an error
    }

    // A command can leave folders it cannot write, as Go's module cache is, and links to anywhere: the home still goes, and what a
    // link leads to stays.
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Releasing_removes_a_home_with_read_only_folders_and_leaves_what_its_links_lead_to()
    {
        var outside = Directory.CreateDirectory(Path.Combine(real.Host, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "kept", Ct);
        var (home, exitCode) = await real.RunAsync(
            $"mkdir -p ~/go/pkg/mod/x && touch ~/go/pkg/mod/x/f && chmod 555 ~/go/pkg/mod/x ~/go/pkg/mod && chmod 444 ~/go/pkg/mod/x/f && ln -s {outside} ~/outside && ln -s {outside}/keep.txt ~/keep && echo $HOME");
        Assert.Equal(0, exitCode);

        real.Sandbox.Release(real.WorkingCopy, []);

        Assert.False(Directory.Exists(home.Trim()));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt"), Ct));
        Assert.Empty(Directory.EnumerateFileSystemEntries(homes));
    }

    // A crash can leave a home whose working copy is gone; it goes as a run starts, and a copy that still exists keeps its home.
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task Releasing_orphans_removes_only_the_homes_of_working_copies_that_are_gone()
    {
        var (kept, _) = await real.RunAsync("echo $HOME");
        var gone = new RealSandbox(real.Sandbox);
        var (orphan, _) = await gone.RunAsync("chmod 500 ~; echo $HOME");
        Directory.Delete(gone.WorkingCopy, recursive: true);
        Directory.CreateDirectory(Path.Combine(homes, "0123456789abcdef")); // a home with no file naming its copy, from before there were any

        real.Sandbox.ReleaseOrphans();

        Assert.True(Directory.Exists(kept.Trim()));
        Assert.False(Directory.Exists(orphan.Trim()));
        Assert.Equal([Path.GetFileName(kept.Trim()), $"{Path.GetFileName(kept.Trim())}.copy"], Directory.EnumerateFileSystemEntries(homes).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Directory.CreateDirectory(gone.WorkingCopy);
        gone.Dispose();
    }

    // A git worktree's .git is a file, which the sandbox hides. The .NET SDK's Source Link reads it and fails the build unless the
    // sandbox turns it off, and the SDK's first-run messages, and socat's warnings, must not reach the output.
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_dotnet_project_in_a_git_worktree_builds_with_only_the_builds_output()
    {
        var git = Path.Combine(real.WorkingCopy, ".git");
        await File.WriteAllTextAsync(git, $"gitdir: {real.Host}/.git/worktrees/copy", Ct);
        await File.WriteAllTextAsync(Path.Combine(real.WorkingCopy, "Lib.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""", Ct);
        await File.WriteAllTextAsync(Path.Combine(real.WorkingCopy, "Lib.cs"), "namespace Lib; public static class Answer { public const int Value = 42; }", Ct);

        // The SDK of the dotnet that runs the tests: three folders above the runtime's.
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        var (output, exitCode) = await real.RunAsync("dotnet build", ["api.nuget.org"], hidden: [git], toolchains: [dotnet]);

        Assert.True(exitCode == 0, output);
        Assert.Contains("Build succeeded.", output, StringComparison.Ordinal);
        foreach (var noise in new[] { "Welcome to .NET", "Telemetry", "certificate", "workloads", "socat", ".git" })
        {
            Assert.DoesNotContain(noise, output, StringComparison.OrdinalIgnoreCase);
        }
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

    // HTTPS goes through the proxy as a CONNECT tunnel, which carries whatever the client sends, so the test's tunnel carries plain
    // HTTP; the proxy checks only its host.
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task The_proxy_tunnels_connections_to_allowed_hosts_only()
    {
        using var server = new Server();

        var (output, _) = await real.RunAsync($"curl -sS -m 10 -p http://127.0.0.1:{server.Port}/; curl -sS -m 10 -p http://example.com/", ["127.0.0.1"]);

        Assert.Contains(Server.Greeting, output, StringComparison.Ordinal);
        Assert.Contains($"[Network: allowed 127.0.0.1:{server.Port}.]", output, StringComparison.Ordinal);
        Assert.Contains("[Network: refused example.com, which is not an allowed host.]", output, StringComparison.Ordinal);
    }

    // SBX-01: the processor limit holds, as a hard cap.
    [Fact(Skip = LinuxOnly, SkipUnless = nameof(OnLinux))]
    public async Task A_command_gets_no_more_processor_time_than_its_limit()
    {
        var (wall, cpu) = await real.TimeWorkAsync(
            "s=$(date +%s%N); i=0; while [ $i -lt 2000000 ]; do i=$((i+1)); done; e=$(date +%s%N); echo WALL $(((e - s) / 1000000)); times");

        Assert.True(cpu > TimeSpan.FromSeconds(0.3) && wall >= cpu * 1.5, $"With half a core, the work took {wall} and used {cpu} of processor time.");
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
        // A new session, a double fork and a plain child: all must end. The host's processes are looked at, so the sleeps' times are
        // this run's own: another run of these tests on the machine at the same time would otherwise be seen too.
        var tag = Random.Shared.Next(100_000, 1_000_000);
        var process = await real.StartAsync($"setsid sleep {tag}1 & (sleep {tag}2 &); sleep {tag}3 & echo started; wait");
        Assert.Equal("started", await process.Output.ReadAsync(Ct));

        await process.DisposeAsync();

        Assert.Empty(HostProcesses($"sleep {tag}"));
    }

    // SBX-06.
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

    /// <summary>The host's processes whose command line contains the text, each with its state and parent, so a failure says why.</summary>
    private static List<string> HostProcesses(string text) =>
        [.. Directory.EnumerateDirectories("/proc").Where(path => int.TryParse(Path.GetFileName(path), out _)).Select(path =>
        {
            try
            {
                var commandLine = File.ReadAllText(Path.Combine(path, "cmdline")).Replace('\0', ' ');
                return commandLine.Contains(text, StringComparison.Ordinal)
                    ? $"{path}: {commandLine}; {string.Join(", ", File.ReadLines(Path.Combine(path, "status")).Where(line => line.Split(':')[0] is "State" or "PPid" or "NSpid"))}"
                    : null;
            }
            catch (IOException)
            {
                return null;
            }
        }).OfType<string>()];
}
