using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Sandbox.Tests;

/// <summary>
/// One agent's sandbox tools behind a real tool pipeline, configured as an application would. Only the boundaries are
/// stand-ins: the sandbox, the human, the secret source, the audit store and the clock.
/// </summary>
internal sealed class Agent : IAsyncDisposable
{
    private readonly ToolPipeline pipeline;
    private readonly string name;

    public Agent(FakeSandbox sandbox, SandboxOptions? options = null, string name = "dev")
    {
        this.name = name;
        Options = Configure(options ?? new());
        Tools = new SandboxTools(sandbox, Options.Capabilities.Sandbox!, Options.Capabilities.Workspace!, name, WorkingCopy);
        var others = new Dictionary<string, ITool>
        {
            ["create_issue"] = CreateIssue,
            ["fetch_document"] = new FakeTool(ToolKind.Read, run: (_, _) => ValueTask.FromResult(ToolResult.Success(Document))),
        };
        pipeline = new ToolPipeline(
            Options, Tools.Tools.Concat(others).ToDictionary(),
            new Dictionary<string, IGate> { [CommandRules.Id] = new CommandRules(Options.Capabilities.Sandbox!) },
            new Dictionary<string, IKnowledgeSource>(),
            Audit, new EventBus(new InMemoryEventLog(), Options.Storage, Time), Human, new InMemorySecretSource(new Dictionary<string, string> { ["NUGET_TOKEN"] = "t0ken" }), Time);
    }

    public OfficinaOptions Options { get; }

    /// <summary>The agent's working copy: a real, empty folder (DESIGN.md §11).</summary>
    public string WorkingCopy { get; } = Directory.CreateTempSubdirectory("officina-copy-").FullName;

    public SandboxTools Tools { get; }

    /// <summary>A write tool that needs a permission the caller does not hold.</summary>
    public FakeTool CreateIssue { get; } = new(ToolKind.Write);

    /// <summary>What the <c>fetch_document</c> tool returns.</summary>
    public string Document { get; set; } = "";

    public ScriptedHuman Human { get; } = new();

    public InMemoryAuditLog Audit { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    /// <summary>The caller holds no permissions, so a tool that needs one is never authorised.</summary>
    public static Caller Owner { get; } = new("owner", null, new HashSet<string>(), new Dictionary<string, string>());

    public async ValueTask DisposeAsync()
    {
        await Tools.DisposeAsync();
        if (Directory.Exists(WorkingCopy))
        {
            Directory.Delete(WorkingCopy, recursive: true);
        }
    }

    public async Task<ToolResult> CallAsync(string tool, object arguments)
    {
        var request = new ToolRequest(tool, JsonSerializer.SerializeToElement(arguments));
        return Assert.Single(await pipeline.RunAsync(new("run-1", name, Owner), [request], TestContext.Current.CancellationToken));
    }

    /// <summary>The sandbox tools under their usual names, with the command rules as the commands' gate, and two other tools.</summary>
    private static OfficinaOptions Configure(SandboxOptions options)
    {
        var tools = new Dictionary<string, ToolOptions>
        {
            ["run_command"] = new() { Source = $"extension:{SandboxTools.Run}", Gates = ["commands"], MaxResultLength = 100 },
            ["start_process"] = new() { Source = $"extension:{SandboxTools.Start}", Gates = ["commands"] },
            ["read_process_output"] = new() { Source = $"extension:{SandboxTools.Read}" },
            ["stop_process"] = new() { Source = $"extension:{SandboxTools.Stop}", GateExemption = "Stops only the agent's own processes." },
            ["create_issue"] = new() { Source = "extension:create_issue", Permissions = ["issues:write"], GateExemption = "Tests only." },
            ["fetch_document"] = new() { Source = "extension:fetch_document" },
        };
        var definition = new AgentDefinition { Instructions = "Work.", Tools = ["all"] };
        return new()
        {
            Agents = new Dictionary<string, AgentDefinition> { ["dev"] = definition, ["reviewer"] = definition },
            Tools = tools,
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = [.. tools.Keys] },
            Gates = new Dictionary<string, GateOptions> { ["commands"] = new() { Use = $"extension:{CommandRules.Id}" } },
            Capabilities = new() { Sandbox = options, Workspace = new() { ProtectedPaths = [new() { Path = "secrets/**" }] } },
        };
    }
}
