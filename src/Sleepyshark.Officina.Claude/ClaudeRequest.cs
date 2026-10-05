using System.Text.Json;
using Anthropic.Models.Beta.Messages;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Lays out one request (CTX-01, CTX-03, ARCHITECTURE §10): the tools sorted by name, each with eager input streaming;
/// the instructions as one system block with a cache point; automatic caching for the tail; then the conversation, where
/// an operator message is a mid-conversation <c>system</c> message (CTX-02) and a block with raw JSON is sent as that JSON,
/// byte for byte (MDL-05).
/// </summary>
internal static class ClaudeRequest
{
    public static MessageCreateParams Build(ClaudeModel model, ModelRequest request)
    {
        var cache = new BetaCacheControlEphemeral { Ttl = model.CacheLifetime == CacheLifetime.OneHour ? Ttl.Ttl1h : Ttl.Ttl5m };
        var typed = new MessageCreateParams
        {
            Model = model.Model,
            MaxTokens = model.MaxOutputTokens,
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = new BetaOutputConfig { Effort = Effort(model.Effort) },
            CacheControl = cache,
            Tools = [.. request.Tools.OrderBy(tool => tool.Name, StringComparer.Ordinal).Select(Tool)],
            System = new List<BetaTextBlockParam> { new() { Text = request.Instructions, CacheControl = cache } },
            Messages = [],
        };
        var body = new Dictionary<string, JsonElement>(typed.RawBodyData) { ["messages"] = Messages(request.Messages) };
        return MessageCreateParams.FromRawUnchecked(typed.RawHeaderData, typed.RawQueryData, body);
    }

    private static Effort Effort(ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => Anthropic.Models.Beta.Messages.Effort.Low,
        ClaudeEffort.Medium => Anthropic.Models.Beta.Messages.Effort.Medium,
        ClaudeEffort.High => Anthropic.Models.Beta.Messages.Effort.High,
        ClaudeEffort.XHigh => Anthropic.Models.Beta.Messages.Effort.Xhigh,
        ClaudeEffort.Max => Anthropic.Models.Beta.Messages.Effort.Max,
        _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown effort."),
    };

    private static BetaToolUnion Tool(Tool tool)
    {
        using var schema = JsonDocument.Parse(tool.InputSchema);
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = InputSchema.FromRawUnchecked(schema.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())),
            EagerInputStreaming = true,
        };
    }

    /// <summary>The messages as raw JSON, so stored blocks reach the wire exactly as they were received.</summary>
    private static JsonElement Messages(IEnumerable<Message> messages)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                if (message.Role == Role.Operator)
                {
                    writer.WriteString("role", "system");
                    writer.WriteString("content", message.Text);
                }
                else
                {
                    writer.WriteString("role", message.Role == Role.User ? "user" : "assistant");
                    writer.WriteStartArray("content");
                    foreach (var block in message.Blocks)
                    {
                        if (block.Raw is not null)
                        {
                            writer.WriteRawValue(block.Raw, skipInputValidation: true);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "text");
                            writer.WriteString("text", block.Text);
                            writer.WriteEndObject();
                        }
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
