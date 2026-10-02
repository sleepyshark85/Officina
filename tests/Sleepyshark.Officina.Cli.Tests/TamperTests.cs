using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// TEST-26, INV-10: an agent cannot change its own or another agent's definition, permissions, budget, rules or checks. With
/// every tool it has, through <c>sof run</c> on a real git repository: the configuration file is read-only to its file tools and
/// to its commands, a task's checks and budget are not its to change, and no tool reaches the configuration at all. The model and
/// the sandbox are the stand-ins.
/// </summary>
public sealed class TamperTests : IDisposable
{
    private const string Configuration = """
        {
          "run": { "permissionMode": "auto" },
          "agents": { "dev": { "instructions": "Work.", "tools": ["all"], "budget": { "turn": { "cost": 1 } } } },
          "tools": {
            "write": { "source": "extension:workspace.write_file", "gateExemption": "Tests only." },
            "run": { "source": "extension:sandbox.run", "gates": ["commands"] },
            "create": { "source": "builtin:tasks.create" },
            "update": { "source": "builtin:tasks.update" }
          },
          "gates": { "commands": { "use": "extension:sandbox.commandRules" } },
          "toolSets": { "all": ["write", "run", "create", "update"] },
          "checks": { "tests": { "command": "dotnet test" } },
          "capabilities": {
            "taskBoard": { "enabled": true },
            "workspace": { "enabled": true },
            "sandbox": { "enabled": true, "commandRules": [{ "match": "*", "action": "allow" }] }
          }
        }
        """;

    private readonly Sof sof = new();
    private readonly ScriptedModelProvider model = new();

    public TamperTests() => sof.Providers["claude"] = model;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => sof.Dispose();

    // The definitions are in sof.json, or in a file it extends, which is as much the configuration (CFG-05).
    [Theory]
    [InlineData("sof.json")]
    [InlineData("team.json")]
    public async Task An_agent_cannot_change_any_definition_permission_budget_rule_or_check(string file)
    {
        sof.Write(file, Configuration);
        if (file != "sof.json")
        {
            sof.Write("sof.json", $$"""{ "extends": ["{{file}}"] }""");
        }

        sof.Commit();
        sof.Sandbox = new FakeSandbox().Reply("", 0);
        var loose = Configuration.Replace("\"cost\": 1", "\"cost\": 1000", StringComparison.Ordinal);
        model.CallTools(("write", $$"""{ "path": "{{file}}", "content": {{System.Text.Json.JsonSerializer.Serialize(loose)}} }"""))
            .CallTools(("run", $$"""{ "command": "echo {} > {{file}}" }"""))
            .CallTools(("create", """{ "id": "t1", "title": "Fix it", "checks": ["tests"], "reason": "plan" }"""))
            .CallTools(("update", """{ "id": "t1", "checks": [], "reason": "The tests are slow." }"""), ("update", """{ "id": "t1", "budget": 100, "reason": "More." }"""))
            .Reply("Done.");

        var (exitCode, output, error) = await sof.RunAsync("run", "--input", "Loosen your limits.");

        Assert.Equal((ExitCodes.Success, ""), (exitCode, error));
        var results = model.Requests[^1].History.SelectMany(message => message.Content).OfType<ToolResultContent>().Select(content => content.Text).ToList();
        Assert.Contains(results, text => text.Contains($"{file} is read-only.", StringComparison.Ordinal)); // the definitions, rules and checks are in it
        var command = Assert.Single(sof.Sandbox.Processes).Command;
        Assert.Contains(Path.Combine(command.Directory, file), command.ReadOnlyPaths); // and the sandbox keeps commands off it
        Assert.Equal(2, results.Count(text => text.Contains("invalid arguments", StringComparison.Ordinal))); // a task's checks and budget are the owner's
        Assert.Equal(Configuration, await File.ReadAllTextAsync(Path.Combine(sof.Directory, file), Ct));
        var runId = output.Split('\n')[0]["run ".Length..];
        var task = (await ((Core.Extensibility.IStorage)await SqliteStorage.OpenAsync(Path.Combine(sof.Directory, ".sof", "sof.db"), Ct)).Tasks.ReadAsync(null, runId, Ct))[^1].Tasks.Single();
        Assert.Equal(["tests"], task.Checks);
        Assert.Equal(8m, task.Budget);
    }
}
