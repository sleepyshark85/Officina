using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>Command checks (WS-02, TASK-05) with the fake sandbox in place of the operating system, in a real folder.</summary>
public sealed class CommandCheckTests : IDisposable
{
    private readonly string copy = Directory.CreateTempSubdirectory("officina-check-").FullName;
    private readonly FakeSandbox sandbox = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(copy, recursive: true);

    // INV-09: the exit code alone decides; the last lines of output are the findings.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task A_command_check_passes_when_its_command_exits_with_zero(int exitCode, bool passes)
    {
        await File.WriteAllTextAsync(Path.Combine(copy, ".env"), "TOKEN=1", Ct);
        sandbox.Reply(string.Join('\n', Enumerable.Range(1, 30).Select(line => $"line {line}")), exitCode);
        var check = new CommandCheck(sandbox, new SandboxOptions { AllowedHosts = ["*.nuget.org"] }, new WorkspaceOptions(), "dotnet test");

        var result = await check.RunAsync(new CheckContext(copy, null, []), Ct);

        Assert.Equal(passes, result.Passed);
        Assert.Equal(passes ? [] : ["dotnet test exited with code 1", .. Enumerable.Range(11, 20).Select(line => $"line {line}")], result.Findings);
        var command = Assert.Single(sandbox.Processes).Command;
        Assert.Equal(("dotnet test", copy), (command.CommandLine, command.Directory));
        Assert.Equal(["*.nuget.org"], command.AllowedHosts);
        Assert.Empty(command.Environment); // a check gets no secrets
        Assert.Equal([Path.Combine(copy, ".env")], command.HiddenPaths); // WS-05
    }

    [Fact]
    public async Task A_command_check_without_a_working_copy_fails_and_runs_nothing()
    {
        var result = await new CommandCheck(sandbox, new SandboxOptions(), new WorkspaceOptions(), "dotnet test").RunAsync(new CheckContext(null, "output", []), Ct);

        Assert.Equal((false, "there is no working copy to run the command in"), (result.Passed, Assert.Single(result.Findings)));
        Assert.Empty(sandbox.Processes);
    }
}
