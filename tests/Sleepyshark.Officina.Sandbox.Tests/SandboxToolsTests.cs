using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>The sandbox tools through the tool pipeline, with the fake sandbox in place of the operating system.</summary>
public sealed class SandboxToolsTests : IAsyncDisposable
{
    private static readonly SandboxOptions Options = new()
    {
        AllowedHosts = ["*.nuget.org"],
        CommandRules = [new() { Match = "dotnet *", Action = CommandAction.Allow }],
        Secrets = new Dictionary<string, IReadOnlyList<string>> { ["dev"] = ["NUGET_TOKEN"] },
    };

    private readonly FakeSandbox sandbox = new();
    private readonly Agent dev;

    public SandboxToolsTests() => dev = new Agent(sandbox, Options);

    public ValueTask DisposeAsync() => dev.DisposeAsync();

    [Fact]
    public async Task A_command_runs_in_the_agents_working_copy_with_its_limits_and_allowed_hosts()
    {
        sandbox.Reply("Build succeeded.", exitCode: 0);

        var result = await dev.CallAsync("run_command", new { command = "dotnet build" });

        Assert.Equal("Build succeeded.\n[exit code 0]", result.Content);
        var command = Assert.Single(sandbox.Processes).Command;
        Assert.Equal(("dotnet build", dev.WorkingCopy, SandboxLimits.Default), (command.CommandLine, command.Directory, command.Limits));
        Assert.Equal(["*.nuget.org"], command.AllowedHosts);
    }

    [Fact]
    public async Task Commands_are_given_the_protected_paths_as_they_are_when_the_command_starts()
    {
        foreach (var path in new[] { ".env", "src/.env.local", "secrets/key.pem", "sof.json", "src/app.cs" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dev.WorkingCopy, path))!);
            await File.WriteAllTextAsync(Path.Combine(dev.WorkingCopy, path), "x", TestContext.Current.CancellationToken);
        }

        Directory.CreateDirectory(Path.Combine(dev.WorkingCopy, ".git"));
        sandbox.Reply("");

        await dev.CallAsync("run_command", new { command = "dotnet build" });

        var command = Assert.Single(sandbox.Processes).Command;
        string[] Relative(IEnumerable<string> paths) => [.. paths.Select(path => Path.GetRelativePath(dev.WorkingCopy, path).Replace('\\', '/')).Order(StringComparer.Ordinal)];
        Assert.Equal([".env", ".git", "secrets/key.pem", "src/.env.local"], Relative(command.HiddenPaths));
        Assert.Equal(["sof.json"], Relative(command.ReadOnlyPaths));
    }

    // SBX-04.
    [Fact]
    public async Task Command_output_is_published_line_by_line_without_secrets()
    {
        sandbox.Reply("Restoring with t0ken\nRestored", exitCode: 0).Reply("Listening", exitCode: null);

        await dev.CallAsync("run_command", new { command = "dotnet restore" });
        await dev.CallAsync("start_process", new { command = "dotnet run" });
        await dev.CallAsync("stop_process", new { id = "p1" });

        var published = (await dev.Events.ReadAsync(null, "run-1", 0, TestContext.Current.CancellationToken))
            .Select(read => read.Payload).OfType<ToolOutput>().Select(output => (output.Tool, output.Line));
        Assert.Equal([("run_command", "Restoring with [secret]"), ("run_command", "Restored"), ("start_process", "Listening")], published);
    }

    [Fact]
    public async Task The_model_receives_trimmed_output_and_the_full_output_is_kept_as_an_artifact()
    {
        sandbox.Reply(string.Join('\n', Enumerable.Repeat("warning: obsolete API", 20)), exitCode: 1);

        var result = await dev.CallAsync("run_command", new { command = "dotnet build" });

        Assert.StartsWith("warning: obsolete API", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("[Trimmed: the first 100 of 453 characters. The full result is artifact 1.]", result.Content, StringComparison.Ordinal);
        Assert.Equal(453, (await dev.Storage.Artifacts.ReadAsync(null, "run-1", 1, TestContext.Current.CancellationToken))!.Content.Length);
    }

    [Fact]
    public async Task Only_the_agents_own_secrets_reach_its_commands_and_never_the_model()
    {
        await using var reviewer = new Agent(sandbox, Options, "reviewer");
        sandbox.Reply("token is t0ken").Reply("");

        var result = await dev.CallAsync("run_command", new { command = "dotnet nuget push" });
        await reviewer.CallAsync("run_command", new { command = "dotnet nuget push" });

        Assert.Equal("t0ken", sandbox.Processes[0].Command.Environment["NUGET_TOKEN"]);
        Assert.Empty(sandbox.Processes[1].Command.Environment);
        Assert.DoesNotContain("t0ken", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(dev.Audit.Entries, entry => entry.Detail?.Contains("t0ken", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_background_process_can_be_started_observed_and_stopped()
    {
        sandbox.Reply("Listening on port 5000", exitCode: null);

        var started = await dev.CallAsync("start_process", new { command = "dotnet run" });
        var running = await dev.CallAsync("read_process_output", new { id = "p1" });
        var again = await dev.CallAsync("read_process_output", new { id = "p1" });
        var stopped = await dev.CallAsync("stop_process", new { id = "p1" });

        Assert.Equal("Started p1.", started.Content);
        Assert.Equal("Listening on port 5000\n[running]", running.Content);
        Assert.Equal("[running]", again.Content);
        Assert.Equal("[exited with code -1]", stopped.Content);
        Assert.True(sandbox.Processes[0].Stopped);
        Assert.Equal(ToolErrorCategory.InvalidArguments, (await dev.CallAsync("read_process_output", new { id = "p1" })).Error);
    }

    [Fact]
    public async Task Background_processes_stop_when_their_owner_ends_and_other_agents_cannot_reach_them()
    {
        await using var reviewer = new Agent(sandbox, Options, "reviewer");
        sandbox.Reply("", exitCode: null).Reply("", exitCode: null);
        await dev.CallAsync("start_process", new { command = "dotnet run" });
        await dev.CallAsync("start_process", new { command = "dotnet watch" });

        var fromReviewer = await reviewer.CallAsync("stop_process", new { id = "p1" });
        await dev.DisposeAsync();

        Assert.Equal(ToolErrorCategory.InvalidArguments, fromReviewer.Error);
        Assert.All(sandbox.Processes, process => Assert.True(process.Stopped));
    }

    [Fact]
    public async Task A_command_is_stopped_at_its_time_limit()
    {
        sandbox.Reply("", exitCode: null);

        var call = dev.CallAsync("run_command", new { command = "dotnet test" });
        var process = await sandbox.FirstStarted;

        dev.Time.Advance(dev.Options.Tools["run_command"].Timeout);

        Assert.Equal(ToolErrorCategory.Timeout, (await call).Error);
        await process.ExitCode;
        Assert.True(process.Stopped);
    }

    [Fact]
    public void Without_isolation_no_command_can_run()
    {
        var unavailable = new FakeSandbox { Problem = "bubblewrap cannot create namespaces." };

        var exception = Assert.Throws<InvalidOperationException>(() => new SandboxTools(unavailable, Options, new(), "dev", "/work/dev"));

        Assert.Equal("Commands cannot run, because this machine cannot sandbox them: bubblewrap cannot create namespaces.", exception.Message);
    }
}
