using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Core.Tests.Tools;

/// <summary>
/// A real tool pipeline for one agent, <c>dev</c>, offered every configured tool. Only the boundaries are stand-ins:
/// the human, the secret source and the clock; storage is the in-memory one.
/// </summary>
internal sealed class ToolSetup
{
    public const string Agent = "dev";

    public static readonly Caller Owner = new("owner", "acme", new HashSet<string> { "issues:write" }, new Dictionary<string, string>());

    /// <summary>A fresh context for each use, so no state of a run, such as its untrusted mark, leaks from one test to another.</summary>
    public static ToolContext Context => new("run-1", Agent, Owner);

    public InMemoryStorage Storage { get; } = new();

    public InMemoryAuditLog Audit => Storage.Audit;

    public InMemoryEventLog Events => Storage.Events;

    public ScriptedHuman Human { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public Dictionary<string, string> Secrets { get; } = [];

    public Dictionary<string, ITool> Tools { get; } = [];

    public Dictionary<string, IGate> Gates { get; } = [];

    public Dictionary<string, IKnowledgeSource> Knowledge { get; } = [];

    public Dictionary<string, ICheck> Checks { get; } = [];

    /// <summary>
    /// A configuration whose agent <c>dev</c> is offered every tool given, in the <c>auto</c> permission mode, so the
    /// rules alone decide; <c>PermissionModeTests</c> test the other modes.
    /// </summary>
    public static OfficinaOptions Options(params (string Name, ToolOptions Tool)[] tools) => new()
    {
        Run = new() { PermissionMode = PermissionMode.Auto },
        Agents = new Dictionary<string, AgentDefinition> { [Agent] = new() { Instructions = "Work.", Tools = ["all"] } },
        Tools = tools.ToDictionary(tool => tool.Name, tool => tool.Tool),
        ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["all"] = [.. tools.Select(tool => tool.Name)] },
    };

    /// <summary>The configuration of a tool the application registers under the same name.</summary>
    public static ToolOptions Extension(string name) => new() { Source = $"extension:{name}" };

    public ToolPipeline Create(OfficinaOptions options) =>
        new(options, Tools, Gates, Knowledge, Checks, Storage, new EventBus(Events, options.Storage, Time), Human, new InMemorySecretSource(Secrets), Time);

    public static JsonElement Args(string text) => JsonDocument.Parse(text).RootElement.Clone();

    public static async Task<ToolResult> RunAsync(ToolPipeline pipeline, string tool, string arguments = "{}", ToolContext? context = null) =>
        Assert.Single(await pipeline.RunAsync(context ?? Context, [new ToolRequest(tool, Args(arguments))], TestContext.Current.CancellationToken));
}
