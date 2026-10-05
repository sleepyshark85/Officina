using System.Text.Json;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>Builds the agents the tests run.</summary>
internal static class Agents
{
    public const string Instructions = "You are a helpful assistant.";

    public const string SearchSchema = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    public static Tool SearchTool(string description = "Searches the catalogue.") => Tool("search", description, SearchSchema);

    /// <summary>A tool whose handler answers <c>ok</c>, or runs <paramref name="handler"/>.</summary>
    public static Tool Tool(
        string name, string description = "A tool.", string schema = """{"type":"object"}""", ToolKind kind = ToolKind.Read,
        Func<JsonElement, CancellationToken, Task<ToolOutput>>? handler = null, bool needsApproval = false) =>
        new(name, description, schema, kind, handler ?? ((_, _) => Task.FromResult(new ToolOutput("ok"))), needsApproval);

    public static AgentDefinition With(ScriptedModel model, string instructions = Instructions, params Tool[] tools) =>
        new() { Model = model, Instructions = instructions, Tools = [.. tools] };

    public static async Task<List<RunEvent>> CollectAsync(IAsyncEnumerable<RunEvent> run)
    {
        var events = new List<RunEvent>();
        await foreach (var runEvent in run)
        {
            events.Add(runEvent);
        }

        return events;
    }
}
