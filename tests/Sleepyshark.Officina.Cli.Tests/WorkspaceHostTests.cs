using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Sandbox;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>What <c>sof</c>'s workspace host does around a task's working copy, on a real git repository; the sandbox is the stand-in.</summary>
public sealed class WorkspaceHostTests : IDisposable
{
    private readonly Sof sof = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    // SBX-03, TASK-05: whatever still runs in a copy stops before it is integrated, even a process started after the copy was sealed
    // at submit, so nothing changes it while integration takes it.
    [Fact]
    public async Task Background_processes_in_a_copy_stop_before_it_is_integrated()
    {
        sof.Write("README.md", "A calculator.\n").Commit();
        var runId = Guid.NewGuid().ToString();
        var sandbox = new FakeSandbox().Reply("", exitCode: null); // runs until it is stopped
        var options = new OfficinaOptions { Capabilities = new() { Workspace = new() { Enabled = true }, Sandbox = new() { Enabled = true } } };
        await using var host = await WorkspaceHost.OpenAsync(
            options, sof.Directory, runId, sandbox, TimeProvider.System, _ => Task.FromResult(false), leaveWorkingCopies: false, TextWriter.Null, Ct);
        var name = WorkingCopies.OfTask(runId, "a");
        var copy = await host.OpenWorkingCopyAsync(name, "developer[1]", Ct);
        var submitted = await host.SealAsync(copy, Ct);

        var call = new ToolCall(
            JsonSerializer.SerializeToElement(new { command = "sleep 1; echo planted > planted.txt" }), Caller.Anonymous, "key",
            new InMemorySecretSource(new Dictionary<string, string>()), null!, null, (_, _) => ValueTask.CompletedTask)
        { Agent = "developer[1]", Definition = "developer", WorkingCopy = name };
        Assert.Null((await host.Tools[SandboxTools.Start].InvokeAsync(call, Ct)).Error);
        var process = Assert.Single(sandbox.Processes);
        Assert.False(process.Stopped);

        var result = await host.IntegrateAsync(copy, "a", "developer[1]", submitted, Ct);

        Assert.Equal(IntegrationOutcome.Integrated, result.Outcome);
        Assert.True(process.Stopped);
    }
}
