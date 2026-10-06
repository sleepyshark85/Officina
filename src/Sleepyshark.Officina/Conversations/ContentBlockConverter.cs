using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>
/// Stores a block's raw JSON as a JSON string, so it reads back exactly, even after a store that normalizes JSON (such
/// as PostgreSQL <c>jsonb</c>) has rewritten the conversation's JSON.
/// </summary>
internal sealed class ContentBlockConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var text = root.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
        var raw = root.TryGetProperty("raw", out var rawElement) ? rawElement.GetString() : null;
        var call = root.TryGetProperty("toolCall", out var callElement) ? callElement.Deserialize<ToolCall>(options) : null;
        var result = root.TryGetProperty("toolResult", out var resultElement) ? resultElement.Deserialize<ToolResult>(options) : null;
        return new ContentBlock(text, raw, call, result);
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Text is not null)
        {
            writer.WriteString("text", value.Text);
        }

        if (value.Raw is not null)
        {
            writer.WriteString("raw", value.Raw);
        }

        if (value.ToolCall is not null)
        {
            writer.WritePropertyName("toolCall");
            JsonSerializer.Serialize(writer, value.ToolCall, options);
        }

        if (value.ToolResult is not null)
        {
            writer.WritePropertyName("toolResult");
            JsonSerializer.Serialize(writer, value.ToolResult, options);
        }

        writer.WriteEndObject();
    }
}
