using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>
/// One piece of a message. A block the model produced keeps the provider's JSON in <see cref="Raw"/>, stored and
/// replayed byte for byte; the core reads only its neutral views, <see cref="Text"/> and <see cref="ToolCall"/>. A
/// block the core made, such as a <see cref="ToolResult"/>, has no raw form; the provider adapter renders it.
/// </summary>
[JsonConverter(typeof(ContentBlockConverter))]
public sealed record ContentBlock
{
    /// <param name="text">The text, for a text block; null for any other.</param>
    /// <param name="raw">The provider's JSON for the block, as received; null for a block the core made.</param>
    /// <param name="toolCall">The neutral view of the call, for a block that requests a tool.</param>
    public ContentBlock(string? text, string? raw = null, ToolCall? toolCall = null)
        : this(text, raw, toolCall, null)
    {
    }

    /// <summary>A tool's result, made by the core.</summary>
    public ContentBlock(ToolResult toolResult)
        : this(null, null, null, toolResult ?? throw new ArgumentNullException(nameof(toolResult)))
    {
    }

    internal ContentBlock(string? text, string? raw, ToolCall? toolCall, ToolResult? toolResult)
    {
        if (text is null && raw is null && toolResult is null)
        {
            throw new ArgumentException("A block needs text, raw JSON or a tool result.");
        }

        if (raw is not null)
        {
            using var _ = JsonDocument.Parse(raw);
        }

        Text = text;
        Raw = raw;
        ToolCall = toolCall;
        ToolResult = toolResult;
    }

    public string? Text { get; }

    public string? Raw { get; }

    public ToolCall? ToolCall { get; }

    public ToolResult? ToolResult { get; }
}
