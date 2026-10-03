using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;
using ClaudeRole = Anthropic.Models.Beta.Messages.Role;
using Content = Sleepyshark.Officina.Core.Messages.Content;
using CoreRole = Sleepyshark.Officina.Core.Messages.Role;

namespace Sleepyshark.Officina.Providers.Claude;

/// <summary>
/// Maps a model request to the Claude API (DESIGN.md §9, CLD-03): the tools, then the instructions and project memory as system blocks,
/// then the history as messages. Each cache boundary becomes a cache marker with its lifetime, and system messages in
/// the history become mid-conversation system messages, which a turn-scoped one asks Claude to clear at the next user
/// message; a model without them gets the operator's messages as user messages. The provider's features add their fields
/// and beta headers (CLD-06). Optional fields are left out, never sent as null.
/// </summary>
internal static class ClaudeRequest
{
    /// <summary>The tools Claude runs itself, by the name <c>provider:</c> sources use, with the version sent (TOOL-13).</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderTools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["web_search"] = "web_search_20260209",
        ["web_fetch"] = "web_fetch_20260209",
        ["code_execution"] = "code_execution_20260521",
    };

    /// <summary>The longest reply when the profile sets none: every call streams, so a long reply cannot time out.</summary>
    private const int DefaultMaxTokens = 64_000;

    private const string TurnScopedBeta = "mid-conversation-system-clear-at-2026-08-21";
    private const string ContextEditingBeta = "context-management-2025-06-27";
    private const string TaskBudgetBeta = "task-budgets-2026-03-13";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private const string CompactionBeta = "compact-2026-09-04";

    /// <summary>What an operator's message starts with when the model reads it as a user message.</summary>
    public const string OperatorLabel = "Message from the operator: ";

    /// <param name="request">The core's request.</param>
    /// <param name="features">The provider's features that are on.</param>
    /// <param name="systemMessages">Whether the model takes a system message in the middle of the conversation.</param>
    public static MessageCreateParams From(ModelRequest request, ProviderFeatures features, bool systemMessages)
    {
        var profile = request.Profile;
        var markers = request.CacheBoundaries.ToDictionary(boundary => boundary.After, boundary => Marker(boundary.Lifetime));
        var cached = markers.ContainsKey(CachePoint.History) ? LastCacheable(request.History) : null;
        var system = new List<BetaTextBlockParam> { Block(request.Instructions, markers.GetValueOrDefault(CachePoint.Instructions)) };
        if (request.Memory.Length > 0)
        {
            system.Add(Block(request.Memory, markers.GetValueOrDefault(CachePoint.Memory)));
        }

        var betas = new List<string>();
        var parameters = new MessageCreateParams
        {
            Model = profile.Model,
            MaxTokens = profile.MaxOutputTokens ?? DefaultMaxTokens,
            Tools = [.. request.Tools.Select(Tool)],
            ToolChoice = profile.ToolChoice == ToolChoice.None ? new BetaToolChoiceNone() : new BetaToolChoiceAuto(),
            System = system,
            Messages = [.. request.History.Select((message, index) =>
                Message(message, index == cached?.Message ? (cached.Value.Block, markers[CachePoint.History]) : null, systemMessages))],
        };
        var format = features.StructuredOutput && request.OutputSchema is { } schema ? new BetaJsonOutputFormat { Schema = Properties(Closed(schema)) } : null;
        var taskBudget = features.TaskBudget is { } total ? new BetaTokenTaskBudget { Total = total } : null;
        if (profile.Effort is not null || format is not null || taskBudget is not null)
        {
            var output = new BetaOutputConfig();
            output = profile.Effort is null ? output : output with { Effort = profile.Effort };
            output = format is null ? output : output with { Format = format };
            output = taskBudget is null ? output : output with { TaskBudget = taskBudget };
            parameters = parameters with { OutputConfig = output };
        }

        if (taskBudget is not null)
        {
            betas.Add(TaskBudgetBeta);
        }

        if (features.ClearToolResults)
        {
            parameters = parameters with { ContextManagement = new BetaContextManagementConfig { Edits = [new BetaClearToolUses20250919Edit()] } };
            betas.Add(ContextEditingBeta);
        }

        if (features.RefusalFallback)
        {
            parameters = parameters with { Fallbacks = new BetaFallbacksParam(new Default()) };
            betas.Add(FallbackBeta);
        }

        if (request.History.Any(message => message.TurnScoped))
        {
            betas.Add(TurnScopedBeta);
        }

        if (request.History.Any(message => message.Content.Any(content => TypeOf(content) == "compaction")))
        {
            betas.Add(CompactionBeta); // every request that carries a compaction block needs it
        }

        return WithSettings(betas.Count == 0 ? parameters : parameters with { Betas = [.. betas] }, profile);
    }

    /// <summary>
    /// HIST-01: a request for a summary of the earlier turns (Claude's compaction on demand). It has the same model, instructions and
    /// tools as the conversation, and nothing a summary call refuses: no output format, no context editing, no fallbacks.
    /// </summary>
    public static MessageCreateParams Compaction(ModelRequest earlier, ProviderFeatures features, bool systemMessages)
    {
        var parameters = From(earlier, features with { StructuredOutput = false, ClearToolResults = false, RefusalFallback = false }, systemMessages);
        var betas = parameters.Betas?.Select(beta => beta.Raw()).ToList() ?? [];
        if (!betas.Contains(CompactionBeta))
        {
            betas.Add(CompactionBeta);
        }

        return parameters with { Compaction = new BetaCompactionConfig(), Betas = [.. betas] };
    }

    /// <summary>The type of a block of Claude's own content, as it came; null for the core's own content.</summary>
    public static string? TypeOf(Content content) =>
        content is ProviderContent provider && provider.Data.TryGetProperty("type", out var type) ? type.GetString() : null;

    /// <summary>CLD-01: any other setting of the API, such as thinking, is sent as it is: as JSON when it is JSON, otherwise as text.</summary>
    private static MessageCreateParams WithSettings(MessageCreateParams parameters, ModelProfile profile)
    {
        if (profile.Settings.Count == 0)
        {
            return parameters;
        }

        var body = new Dictionary<string, JsonElement>(parameters.RawBodyData);
        foreach (var (name, value) in profile.Settings)
        {
            body[name] = Json(value);
        }

        return MessageCreateParams.FromRawUnchecked(parameters.RawHeaderData, parameters.RawQueryData, body);
    }

    /// <summary>
    /// Claude's structured output takes an object schema only with <c>additionalProperties: false</c>, so it is added to each schema
    /// whose <c>type</c> is or includes <c>object</c> and that leaves it unset, as the Anthropic SDKs do. A reply this allows also matches the schema as written, which the core
    /// checks. Any other keyword Claude does not take, such as <c>additionalProperties: true</c> or <c>minLength</c>, is sent as it is,
    /// and Claude refuses the call as an invalid request.
    /// </summary>
    private static JsonElement Closed(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText());
        Close(node);
        return JsonSerializer.SerializeToElement(node);

        static void Close(JsonNode? node)
        {
            if (node is not JsonObject schema)
            {
                return;
            }

            var type = schema["type"];
            // Only a schema whose type says object: allOf branches that list properties without a type stay open, since closing
            // each would allow no property of the others.
            var isObject = type is JsonValue value && value.TryGetValue<string>(out var name) && name == "object"
                || type is JsonArray types && types.Any(each => each?.GetValueKind() == JsonValueKind.String && each.GetValue<string>() == "object");
            if (isObject && !schema.ContainsKey("additionalProperties"))
            {
                schema["additionalProperties"] = false;
            }

            foreach (var key in new[] { "properties", "$defs", "definitions" })
            {
                foreach (var (_, child) in schema[key] as JsonObject ?? [])
                {
                    Close(child);
                }
            }

            foreach (var key in new[] { "anyOf", "allOf", "prefixItems" })
            {
                foreach (var child in schema[key] as JsonArray ?? [])
                {
                    Close(child);
                }
            }

            Close(schema["items"]);
        }
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object ? schema.EnumerateObject().ToDictionary(property => property.Name, property => property.Value) : [];

    private static BetaTextBlockParam Block(string text, BetaCacheControlEphemeral? marker) =>
        marker is null ? new BetaTextBlockParam { Text = text } : new BetaTextBlockParam { Text = text, CacheControl = marker };

    /// <summary>Claude keeps a cache for five minutes or an hour; a lifetime longer than five minutes gets the hour.</summary>
    private static BetaCacheControlEphemeral Marker(TimeSpan lifetime) => new() { Ttl = lifetime > TimeSpan.FromMinutes(5) ? Ttl.Ttl1h : Ttl.Ttl5m };

    /// <summary>
    /// Where boundary ③ goes: the last text, tool call or tool result in the history. A turn-scoped message cannot carry a
    /// marker, and reasoning and provider content are not cached on their own.
    /// </summary>
    private static (int Message, int Block)? LastCacheable(IReadOnlyList<Message> history)
    {
        for (var message = history.Count - 1; message >= 0; message--)
        {
            for (var block = history[message].Content.Length - 1; !history[message].TurnScoped && block >= 0; block--)
            {
                if (history[message].Content[block] is TextContent or ToolUseContent or ToolResultContent)
                {
                    return (message, block);
                }
            }
        }

        return null;
    }

    private static BetaToolUnion Tool(ToolDefinition tool)
    {
        if (tool.ProviderTool is not { } name)
        {
            var schema = tool.InputSchema ?? JsonSerializer.SerializeToElement(new { type = "object" });
            var definition = new BetaTool
            {
                Name = tool.Name,
                InputSchema = new InputSchema(Properties(schema)),
                EagerInputStreaming = true,
            };
            return tool.Description is null ? definition : definition with { Description = tool.Description };
        }

        var json = new JsonObject { ["type"] = ProviderTools[name], ["name"] = name };
        if (tool.Limits?.MaxUses is { } maxUses)
        {
            json["max_uses"] = maxUses;
        }

        if (tool.Limits?.AllowedDomains is { Count: > 0 } domains)
        {
            json["allowed_domains"] = new JsonArray([.. domains.Select(domain => JsonValue.Create(domain))]);
        }

        return json.Deserialize<BetaToolUnion>()!;
    }

    private static BetaMessageParam Message(Message message, (int Block, BetaCacheControlEphemeral Marker)? cached, bool systemMessages)
    {
        // A model without mid-conversation system messages rejects them, so it reads the operator's as a user message, labelled.
        var operatorAsUser = message.Role == CoreRole.System && !systemMessages;
        var mapped = new BetaMessageParam
        {
            Role = message.Role switch
            {
                CoreRole.User => ClaudeRole.User,
                CoreRole.Assistant => ClaudeRole.Assistant,
                _ when operatorAsUser => ClaudeRole.User,
                _ => ClaudeRole.System,
            },
            Content = Kept(message).Select(kept =>
            {
                var block = kept.Index == cached?.Block ? Mark(kept.Content, cached.Value.Marker) : Block(kept.Content);
                return operatorAsUser && kept.Index == 0 && block.TryPickText(out var text) ? text with { Text = OperatorLabel + text.Text } : block;
            }).ToList(),
        };
        return message.TurnScoped ? mapped with { ClearAt = ClearAt.NextUserMessage } : mapped;
    }

    /// <summary>
    /// What of a message goes back to Claude. After a refusal fallback, the API asks that what the declining model produced before the
    /// last <c>fallback</c> block be left out, but its text and its server tool calls with their results: its reasoning, its tool calls,
    /// and its server tool calls without a result. Everything else goes back as it came, the block where it was.
    /// </summary>
    private static IEnumerable<(Content Content, int Index)> Kept(Message message)
    {
        var lastFallback = message.Content.Select(TypeOf).ToList().LastIndexOf("fallback");
        var answered = message.Content.OfType<ProviderContent>().Select(content => content.Data.TryGetProperty("tool_use_id", out var id) ? id.GetString() : null).ToHashSet();
        return message.Content.Select((content, index) => (content, index)).Where(kept => kept.index > lastFallback
            || kept.content is TextContent
            || TypeOf(kept.content) is "fallback"
            || kept.content is ProviderContent provider && provider.Data.TryGetProperty("tool_use_id", out _)
            || kept.content is ProviderContent server && TypeOf(server) == "server_tool_use" && answered.Contains(server.Data.GetProperty("id").GetString()));
    }

    /// <summary>Reasoning goes back exactly as received (CLD-05), and content of Claude's own as the JSON it came in.</summary>
    private static BetaContentBlockParam Block(Content content) => content switch
    {
        TextContent text => new BetaTextBlockParam { Text = text.Text },
        ReasoningContent reasoning => new BetaThinkingBlockParam { Thinking = reasoning.Text, Signature = reasoning.Signature ?? "" },
        ToolUseContent call => new BetaToolUseBlockParam
        {
            ID = call.Id,
            Name = call.Name,
            Input = call.Arguments.ValueKind == JsonValueKind.Object ? call.Arguments.EnumerateObject().ToDictionary(property => property.Name, property => property.Value) : [],
        },
        ToolResultContent { IsError: true } result => new BetaToolResultBlockParam { ToolUseID = result.ToolUseId, Content = result.Text, IsError = true },
        ToolResultContent result => new BetaToolResultBlockParam { ToolUseID = result.ToolUseId, Content = result.Text },
        ProviderContent provider => provider.Data.Deserialize<BetaContentBlockParam>()!,
        _ => throw new NotSupportedException($"Claude has no form for {content.GetType().Name}."),
    };

    /// <summary>The block with a cache marker; <see cref="LastCacheable"/> picks only blocks that can carry one.</summary>
    private static BetaContentBlockParam Mark(Content content, BetaCacheControlEphemeral marker)
    {
        var block = Block(content);
        return block.TryPickText(out var text) ? text with { CacheControl = marker }
            : block.TryPickToolUse(out var call) ? call with { CacheControl = marker }
            : block.TryPickToolResult(out var result) ? result with { CacheControl = marker }
            : block;
    }

    private static JsonElement Json(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(value);
        }
    }
}
