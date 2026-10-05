using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>Builds the agents the tests run.</summary>
internal static class Agents
{
    public const string Instructions = "You are a helpful assistant.";

    public static Tool SearchTool(string description = "Searches the catalogue.") =>
        new("search", description, """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""");

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
