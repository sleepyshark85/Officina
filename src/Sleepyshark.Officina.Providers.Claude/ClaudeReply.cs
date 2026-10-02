using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic.Models.Beta.Messages;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;
using CoreUsage = Sleepyshark.Officina.Core.Messages.Usage;

namespace Sleepyshark.Officina.Providers.Claude;

/// <summary>
/// Reads one streamed reply. Text is passed on as it arrives (MDL-07); every other block once it is complete, in the
/// order of the reply, so the reply goes back to Claude as it came. A tool's input streams unchecked (eager input
/// streaming), and the tool pipeline checks it against the tool's schema before the tool runs.
/// </summary>
internal sealed class ClaudeReply
{
    private readonly ImmutableArray<ToolDefinition> tools;
    private readonly ModelProfile profile;
    private readonly Dictionary<string, ToolRequest> providerCalls = new(StringComparer.Ordinal);
    private readonly StringBuilder thinking = new();
    private readonly StringBuilder input = new();
    private JsonObject? block;
    private string? signature;
    private int toolUses;
    private BetaUsage? started;

    /// <param name="tools">The tools the request offers.</param>
    /// <param name="history">
    /// The request's history. A paused reply (<c>pause_turn</c>) can end with a server tool's call, whose result arrives in
    /// the next reply, so the calls of the last assistant message that have no result yet are waiting for one.
    /// </param>
    /// <param name="profile">The profile the call was made with; a refusal fallback reports the model that served it under it.</param>
    public ClaudeReply(ImmutableArray<ToolDefinition> tools, ImmutableArray<Message> history, ModelProfile profile)
    {
        this.tools = tools;
        this.profile = profile;
        var data = history.LastOrDefault(message => message.Role == Core.Messages.Role.Assistant)?.Content.OfType<ProviderContent>().Select(content => content.Data).ToList() ?? [];
        var answered = data.Select(block => block.TryGetProperty("tool_use_id", out var id) ? id.GetString() : null).ToHashSet(StringComparer.Ordinal);
        foreach (var block in data.Where(block => block.TryGetProperty("type", out var type) && type.GetString() == "server_tool_use"))
        {
            if (!answered.Contains(block.GetProperty("id").GetString()))
            {
                providerCalls[block.GetProperty("id").GetString()!] = Request(block);
            }
        }
    }

    public IEnumerable<ModelEvent> Read(BetaRawMessageStreamEvent streamEvent)
    {
        if (streamEvent.TryPickStart(out var start))
        {
            started = start.Message.Usage;
        }
        else if (streamEvent.TryPickContentBlockStart(out var blockStart))
        {
            block = JsonNode.Parse(blockStart.ContentBlock.Json.GetRawText())!.AsObject();
            (signature, thinking.Length, input.Length) = (null, 0, 0);
            if (block["type"]?.GetValue<string>() == "text" && block["text"]?.GetValue<string>() is { Length: > 0 } text)
            {
                yield return new TextDelta(text);
            }
        }
        else if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
        {
            if (blockDelta.Delta.TryPickText(out var text))
            {
                yield return new TextDelta(text.Text);
            }
            else if (blockDelta.Delta.TryPickThinking(out var reasoning))
            {
                thinking.Append(reasoning.Thinking);
            }
            else if (blockDelta.Delta.TryPickSignature(out var signed))
            {
                signature = signed.Signature;
            }
            else if (blockDelta.Delta.TryPickInputJson(out var json))
            {
                input.Append(json.PartialJson);
            }
            else if (blockDelta.Delta.TryPickCompaction(out _) && block is not null)
            {
                // A compaction block's summary can arrive in pieces, its opaque content too; the block goes back with all of it.
                foreach (var field in new[] { "content", "encrypted_content" })
                {
                    if (blockDelta.Delta.Json.TryGetProperty(field, out var piece) && piece.ValueKind == JsonValueKind.String)
                    {
                        block[field] = (block[field]?.GetValue<string>() ?? "") + piece.GetString();
                    }
                }
            }
        }
        else if (streamEvent.TryPickContentBlockStop(out _) && block is not null)
        {
            foreach (var modelEvent in Complete(block))
            {
                yield return modelEvent;
            }

            block = null;
        }
        else if (streamEvent.TryPickDelta(out var messageDelta))
        {
            foreach (var spent in Spent(messageDelta.Usage))
            {
                yield return spent;
            }

            yield return new Stopped(Stop(messageDelta.Delta.StopReason?.Raw()));
        }
    }

    /// <summary>CLD-04: every stop reason Claude has; anything else is unknown, not an error.</summary>
    private static StopReason Stop(string? reason) => reason switch
    {
        "end_turn" => StopReason.Finished,
        "tool_use" => StopReason.WantsTools,
        "max_tokens" => StopReason.OutputLimit,
        "stop_sequence" => StopReason.StopSequence,
        "pause_turn" => StopReason.Paused,
        "refusal" => StopReason.Refused,
        "model_context_window_exceeded" => StopReason.InputTooLong,
        _ => StopReason.Unknown,
    };

    /// <summary>
    /// The block, complete. Reasoning keeps its signature (CLD-05); content of Claude's own, such as a server tool's call
    /// and result, is kept as its JSON, and a result is reported as a call the provider ran (TOOL-13).
    /// </summary>
    private IEnumerable<ModelEvent> Complete(JsonObject complete)
    {
        var type = complete["type"]?.GetValue<string>();
        if (type == "text")
        {
            yield break;
        }

        if (type == "thinking")
        {
            yield return new ContentReceived(new ReasoningContent(thinking.ToString(), signature));
            yield break;
        }

        if (input.Length > 0)
        {
            complete["input"] = Input();
        }

        if (type == "compaction" && signature is not null)
        {
            complete["signature"] = signature;
        }

        if (type == "fallback")
        {
            // CLD-06: the declining model's tool calls are not run, and its server tool calls without a result are not waited for.
            if (toolUses > 0)
            {
                yield return new ToolCallsWithdrawn();
            }

            toolUses = 0;
            providerCalls.Clear();
        }

        if (type == "tool_use")
        {
            toolUses++;
            yield return new ContentReceived(new ToolUseContent(
                complete["id"]!.GetValue<string>(), complete["name"]!.GetValue<string>(), JsonSerializer.SerializeToElement(complete["input"] ?? new JsonObject())));
            yield break;
        }

        var data = JsonSerializer.SerializeToElement(complete);
        yield return new ContentReceived(new ProviderContent(data));
        if (type == "server_tool_use")
        {
            providerCalls[complete["id"]!.GetValue<string>()] = Request(data);
        }
        else if (complete["tool_use_id"]?.GetValue<string>() is { } id && providerCalls.Remove(id, out var call))
        {
            yield return new ProviderToolUsed(call, complete["content"]?.ToJsonString() ?? "");
        }
    }

    /// <summary>A server tool's call. Claude names its tools by their own names; the core knows them by their configured ones.</summary>
    private ToolRequest Request(JsonElement call)
    {
        var name = call.GetProperty("name").GetString()!;
        return new ToolRequest(tools.FirstOrDefault(tool => tool.ProviderTool == name)?.Name ?? name, call.GetProperty("input"));
    }

    /// <summary>The streamed input; input cut off by the output limit is kept as text, which no tool's schema accepts.</summary>
    private JsonNode? Input()
    {
        try
        {
            return JsonNode.Parse(input.ToString());
        }
        catch (JsonException)
        {
            return JsonValue.Create(input.ToString());
        }
    }

    /// <summary>
    /// What the call used. A call served by a refusal fallback, or a compaction, lists each attempt in <c>usage.iterations</c>, each
    /// billed at its own model's rates, while the totals count only the last: each attempt is reported in turn, and a change of
    /// model before it, so the core prices each by its model (CLD-06, CLD-07).
    /// </summary>
    private IEnumerable<ModelEvent> Spent(BetaMessageDeltaUsage end)
    {
        if (!end.RawData.TryGetValue("iterations", out var iterations) || iterations.ValueKind != JsonValueKind.Array || iterations.GetArrayLength() == 0
            || (iterations.GetArrayLength() == 1 && iterations[0].TryGetProperty("type", out var only) && only.GetString() == "message"))
        {
            yield return new UsageReported(Usage(end));
            yield break;
        }

        var model = profile.Model;
        foreach (var iteration in iterations.EnumerateArray())
        {
            if (iteration.TryGetProperty("model", out var served) && served.GetString() is { } name && name != model)
            {
                model = name;
                yield return new FallbackUsed(name, profile with { Model = name }, ModelFailure.Refused);
            }

            var oneHour = iteration.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object ? Tokens(creation, "ephemeral_1h_input_tokens") : 0;
            yield return new UsageReported(new(Tokens(iteration, "input_tokens"), Tokens(iteration, "output_tokens"), Tokens(iteration, "cache_read_input_tokens"),
                Tokens(iteration, "cache_creation_input_tokens"), oneHour));
        }

        static long Tokens(JsonElement iteration, string name) =>
            iteration.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt64() : 0;
    }

    /// <summary>
    /// CLD-07: the tokens of the whole call. The counts at the end are totals that include any server tool's work; the
    /// split of cache writes by lifetime is known from the start only.
    /// </summary>
    private CoreUsage Usage(BetaMessageDeltaUsage end) => new(
        end.InputTokens ?? started?.InputTokens ?? 0,
        end.OutputTokens,
        end.CacheReadInputTokens ?? started?.CacheReadInputTokens ?? 0,
        end.CacheCreationInputTokens ?? started?.CacheCreationInputTokens ?? 0,
        started?.CacheCreation?.Ephemeral1hInputTokens ?? 0);
}
