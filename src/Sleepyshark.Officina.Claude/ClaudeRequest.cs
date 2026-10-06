using System.Text.Json;
using Anthropic.Core;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Lays out one request: the tools sorted by name with eager input streaming, the memory tool as Claude's native
/// <c>memory_20250818</c>; the instructions as one cached system block; automatic caching for the tail; then the
/// conversation, where an operator message becomes a mid-conversation <c>system</c> message and raw blocks are sent byte
/// for byte. A tool result is a <c>tool_result</c> block, with <c>is_error</c> on failure; a user message after tool
/// results is a second user turn, which the API joins to the first. A typed output schema goes as the structured output
/// format, adjusted; the tool choice is never forced.
/// </summary>
internal static class ClaudeRequest
{
    public static MessageCreateParams Build(ClaudeModel model, ModelRequest request)
    {
        var cache = new BetaCacheControlEphemeral { Ttl = model.CacheLifetime == CacheLifetime.OneHour ? Ttl.Ttl1h : Ttl.Ttl5m };
        var typed = new MessageCreateParams
        {
            Model = model.Model,
            MaxTokens = Math.Min(model.MaxOutputTokens, request.MaxOutputTokens ?? int.MaxValue),
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = request.Prefix.OutputSchema is null
                ? new BetaOutputConfig { Effort = Effort(model.Effort) }
                : new BetaOutputConfig { Effort = Effort(model.Effort), Format = new BetaJsonOutputFormat { Schema = OutputSchema.Adjust(request.Prefix.OutputSchema) } },
            CacheControl = cache,
            Tools = [.. request.Prefix.Tools.Select(Tool)],
            System = new List<BetaTextBlockParam> { new() { Text = request.Prefix.Instructions, CacheControl = cache } },
            Messages = [],
        };
        // An empty setting asks for nothing, so the request has no context management.
        if (request.Prefix.ContextManagement is { } context && Edits(context) is { Count: > 0 } edits)
        {
            typed = typed with { Betas = Betas(context), ContextManagement = new BetaContextManagementConfig { Edits = edits } };
        }

        var body = new Dictionary<string, JsonElement>(typed.RawBodyData) { ["messages"] = Messages(request.Messages) };
        return MessageCreateParams.FromRawUnchecked(typed.RawHeaderData, typed.RawQueryData, body);
    }

    /// <summary>The beta features <paramref name="context"/> uses.</summary>
    private static List<ApiEnum<string, AnthropicBeta>> Betas(ContextManagement context) =>
    [
        .. context.ClearToolResults is null ? [] : new ApiEnum<string, AnthropicBeta>[] { "context-management-2025-06-27" },
        .. context.CompactAt is null ? [] : new ApiEnum<string, AnthropicBeta>[] { "compact-2026-01-12" },
    ];

    /// <summary>Tool-result clearing, then threshold compaction, whose trigger must be at least 50,000 input tokens.</summary>
    private static List<Edit> Edits(ContextManagement context)
    {
        var edits = new List<Edit>();
        if (context.ClearToolResults is { } clearing)
        {
            edits.Add(new BetaClearToolUses20250919Edit
            {
                Trigger = new BetaToolUsesTrigger { Value = clearing.After },
                Keep = new BetaToolUsesKeep { Value = clearing.Keep },
                ClearAtLeast = clearing.AtLeastTokens > 0 ? new BetaInputTokensClearAtLeast { Value = clearing.AtLeastTokens } : null,
            });
        }

        if (context.CompactAt is { } tokens)
        {
            edits.Add(new BetaCompact20260112Edit { Trigger = new BetaInputTokensTrigger { ValueValue = tokens } });
        }

        return edits;
    }

    /// <summary>The effort as the API names it.</summary>
    public static string EffortWord(ClaudeEffort effort) => Effort(effort).ToString().ToLowerInvariant();

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
        // The memory tool is Claude's own, which the model is trained on: it carries no schema or description of ours.
        if (tool.IsMemory)
        {
            return new BetaMemoryTool20250818();
        }

        using var schema = JsonDocument.Parse(tool.InputSchema);
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = InputSchema.FromRawUnchecked(schema.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone())),
            EagerInputStreaming = true,
        };
    }

    /// <summary>The messages as raw JSON, so stored blocks reach the wire exactly as received.</summary>
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
                        else if (block.ToolResult is { } result)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", "tool_result");
                            writer.WriteString("tool_use_id", result.CallId);
                            if (result.Content.Length > 0)
                            {
                                writer.WriteString("content", result.Content);
                            }

                            if (result.IsError)
                            {
                                writer.WriteBoolean("is_error", true);
                            }

                            writer.WriteEndObject();
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
