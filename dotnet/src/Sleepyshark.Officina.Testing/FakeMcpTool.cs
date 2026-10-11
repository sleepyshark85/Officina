using System.Text.Json;

namespace Sleepyshark.Officina.Testing;

/// <summary>A tool of a <see cref="FakeMcpServer"/>: its handler's text is the result; an exception is an error result.</summary>
/// <param name="Name">The tool's name on the server.</param>
/// <param name="Handler">Answers a call's arguments.</param>
public sealed record FakeMcpTool(string Name, Func<JsonElement, string> Handler)
{
    public string Description { get; init; } = $"The {Name} tool.";

    public string InputSchema { get; init; } = """{"type":"object","properties":{"text":{"type":"string"}}}""";

    /// <summary>The <c>readOnlyHint</c> annotation; null lists no annotations.</summary>
    public bool? ReadOnly { get; init; }
}
