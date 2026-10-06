using System.Text.Json;
using Anthropic.Core;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using ApiRole = Anthropic.Models.Beta.Messages.Role;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Lays out one request: the tools sorted by name with eager input streaming, the memory tool as Claude's native
/// <c>memory_20250818</c>; the instructions as one cached system block, with the prefix's lifetime; automatic caching
/// for the tail, with the conversation's; then the
/// conversation, where an operator message becomes a mid-conversation <c>system</c> message and raw blocks are sent byte
/// for byte. A tool result is a <c>tool_result</c> block, with <c>is_error</c> on failure; a user message after tool
/// results is a second user turn, which the API joins to the first. A typed output schema goes as the structured output
/// format, adjusted; the tool choice is never forced.
/// </summary>
internal static class ClaudeRequest
{
    public static MessageCreateParams Build(ClaudeModel model, ModelRequest request)
    {
        if (model.PrefixCacheLifetime == CacheLifetime.FiveMinutes && model.ConversationCacheLifetime == CacheLifetime.OneHour)
        {
            throw new InvalidOperationException("The prefix's cache lifetime cannot be shorter than the conversation's: the API requires longer-lived cache entries first.");
        }

        var prefixCache = new BetaCacheControlEphemeral { Ttl = CacheTtl(model.PrefixCacheLifetime) };
        var typed = new MessageCreateParams
        {
            Model = model.Model,
            MaxTokens = Math.Min(model.MaxOutputTokens, request.MaxOutputTokens ?? int.MaxValue),
            Thinking = new BetaThinkingConfigAdaptive(),
            OutputConfig = request.Prefix.OutputSchema is null
                ? new BetaOutputConfig { Effort = Effort(model.Effort) }
                : new BetaOutputConfig { Effort = Effort(model.Effort), Format = new BetaJsonOutputFormat { Schema = OutputSchema.Adjust(request.Prefix.OutputSchema) } },
            CacheControl = new BetaCacheControlEphemeral { Ttl = CacheTtl(model.ConversationCacheLifetime) },
            Tools = [.. request.Prefix.Tools.Select(Tool)],
            System = new List<BetaTextBlockParam> { new() { Text = request.Prefix.Instructions, CacheControl = prefixCache } },
            Messages = [.. request.Messages.Select(Message)],
        };
        // An empty setting asks for nothing, so the request has no context management.
        if (request.Prefix.ContextManagement is { } context && Edits(context) is { Count: > 0 } edits)
        {
            typed = typed with { Betas = Betas(context), ContextManagement = new BetaContextManagementConfig { Edits = edits } };
        }

        return typed;
    }

    /// <summary>The beta features <paramref name="context"/> uses.</summary>
    private static List<ApiEnum<string, AnthropicBeta>> Betas(ContextManagement context) =>
    [
        .. context.ClearToolResults is null ? [] : new ApiEnum<string, AnthropicBeta>[] { AnthropicBeta.ContextManagement2025_06_27 },
        .. context.CompactAt is null ? [] : new ApiEnum<string, AnthropicBeta>[] { AnthropicBeta.Compact2026_01_12 },
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
    public static string EffortWord(ClaudeEffort effort) => ((ApiEnum<string, Effort>)Effort(effort)).Raw();

    /// <summary>
    /// The cache lifetimes as the API names them: one word when they are the same, as before they could differ, so a
    /// stored conversation keeps its prefix fingerprint; otherwise the prefix's, then the conversation's.
    /// </summary>
    public static string CacheWords(ClaudeModel model) => model.PrefixCacheLifetime == model.ConversationCacheLifetime
        ? CacheWord(model.PrefixCacheLifetime)
        : $"{CacheWord(model.PrefixCacheLifetime)}/{CacheWord(model.ConversationCacheLifetime)}";

    private static string CacheWord(CacheLifetime lifetime) => ((ApiEnum<string, Ttl>)CacheTtl(lifetime)).Raw();

    private static Ttl CacheTtl(CacheLifetime lifetime) => lifetime == CacheLifetime.OneHour ? Ttl.Ttl1h : Ttl.Ttl5m;

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

    /// <summary>A message as the API takes it: an operator message is a system message; stored blocks go as received.</summary>
    private static BetaMessageParam Message(Message message) => message.Role == Role.Operator
        ? new BetaMessageParam { Role = ApiRole.System, Content = message.Text }
        : new BetaMessageParam { Role = message.Role == Role.User ? ApiRole.User : ApiRole.Assistant, Content = message.Blocks.Select(Block).ToList() };

    private static BetaContentBlockParam Block(ContentBlock block)
    {
        if (block.Raw is not null)
        {
            using var raw = JsonDocument.Parse(block.Raw);
            return new BetaContentBlockParam(raw.RootElement.Clone());
        }

        if (block.ToolResult is not { } result)
        {
            return new BetaTextBlockParam { Text = block.Text! };
        }

        // Empty content and success are left out, as the API reads their absence that way.
        var param = new BetaToolResultBlockParam { ToolUseID = result.CallId };
        param = result.Content.Length > 0 ? param with { Content = result.Content } : param;
        return result.IsError ? param with { IsError = true } : param;
    }
}
