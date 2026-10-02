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
/// message. Optional fields are left out, never sent as null.
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

    public static MessageCreateParams From(ModelRequest request)
    {
        var profile = request.Profile;
        var markers = request.CacheBoundaries.ToDictionary(boundary => boundary.After, boundary => Marker(boundary.Lifetime));
        var cached = markers.ContainsKey(CachePoint.History) ? LastCacheable(request.History) : null;
        var system = new List<BetaTextBlockParam> { Block(request.Instructions, markers.GetValueOrDefault(CachePoint.Instructions)) };
        if (request.Memory.Length > 0)
        {
            system.Add(Block(request.Memory, markers.GetValueOrDefault(CachePoint.Memory)));
        }

        var parameters = new MessageCreateParams
        {
            Model = profile.Model,
            MaxTokens = profile.MaxOutputTokens ?? DefaultMaxTokens,
            Tools = [.. request.Tools.Select(Tool)],
            ToolChoice = profile.ToolChoice == ToolChoice.None ? new BetaToolChoiceNone() : new BetaToolChoiceAuto(),
            System = system,
            Messages = [.. request.History.Select((message, index) => Message(message, index == cached?.Message ? (cached.Value.Block, markers[CachePoint.History]) : null))],
        };
        if (profile.Effort is not null)
        {
            parameters = parameters with { OutputConfig = new BetaOutputConfig { Effort = profile.Effort } };
        }

        if (request.History.Any(message => message.TurnScoped))
        {
            parameters = parameters with { Betas = [TurnScopedBeta] };
        }

        if (profile.Settings.Count == 0)
        {
            return parameters;
        }

        // CLD-01: any other setting of the API, such as thinking, is sent as it is: as JSON when it is JSON, otherwise as text.
        var body = new Dictionary<string, JsonElement>(parameters.RawBodyData);
        foreach (var (name, value) in profile.Settings)
        {
            body[name] = Json(value);
        }

        return MessageCreateParams.FromRawUnchecked(parameters.RawHeaderData, parameters.RawQueryData, body);
    }

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
                InputSchema = new InputSchema(schema.EnumerateObject().ToDictionary(property => property.Name, property => property.Value)),
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

    private static BetaMessageParam Message(Message message, (int Block, BetaCacheControlEphemeral Marker)? cached)
    {
        var mapped = new BetaMessageParam
        {
            Role = message.Role switch
            {
                CoreRole.User => ClaudeRole.User,
                CoreRole.Assistant => ClaudeRole.Assistant,
                _ => ClaudeRole.System,
            },
            Content = message.Content.Select((content, index) => index == cached?.Block ? Mark(content, cached.Value.Marker) : Block(content)).ToList(),
        };
        return message.TurnScoped ? mapped with { ClearAt = ClearAt.NextUserMessage } : mapped;
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
