using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>The neutral view of a tool call the model requested.</summary>
/// <param name="Id">The provider's id for the call, which its result answers.</param>
/// <param name="Name">The tool's name.</param>
/// <param name="Input">The input, as JSON text.</param>
public sealed record ToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("input")] string Input);
