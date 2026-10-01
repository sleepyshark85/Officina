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
        Assert.Equal(("dotnet build", Agent.WorkingCopy, SandboxLimits.Default), (command.CommandLine, command.Directory, command.Limits));
        Assert.Equal(["*.nuget.org"], command.AllowedHosts);
    }

    [Fact]
    public async Task The_model_receives_trimmed_output()
    {
        sandbox.Reply(string.Join('\n', Enumerable.Repeat("warning: obsolete API", 20)), exitCode: 1);

        var result = await dev.CallAsync("run_command", new { command = "dotnet build" });

        Assert.StartsWith("warning: obsolete API", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("[Trimmed: the first 100 of 453 characters.]", result.Content, StringComparison.Ordinal);
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
        while (sandbox.Processes.Count == 0)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        dev.Time.Advance(dev.Options.Tools["run_command"].Timeout);

        Assert.Equal(ToolErrorCategory.Timeout, (await call).Error);
        await sandbox.Processes[0].ExitCode;
        Assert.True(sandbox.Processes[0].Stopped);
    }

    [Fact]
    public void Without_isolation_no_command_can_run()
    {
        var unavailable = new FakeSandbox { Problem = "bubblewrap cannot create namespaces." };

        var exception = Assert.Throws<InvalidOperationException>(() => new SandboxTools(unavailable, Options, "dev", Agent.WorkingCopy));

        Assert.Equal("Commands cannot run, because this machine cannot sandbox them: bubblewrap cannot create namespaces.", exception.Message);
    }
}
