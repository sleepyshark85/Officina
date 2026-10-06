using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>The result of a tool call, as the model gets it; any failure is an error result.</summary>
/// <param name="CallId">The id of the call it answers.</param>
/// <param name="Content">What the tool returned, or why the call failed.</param>
/// <param name="IsError">Whether the call failed.</param>
public sealed record ToolResult(
    [property: JsonPropertyName("callId")] string CallId,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("isError")] bool IsError);
