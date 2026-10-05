using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Reads a complete streamed reply: each content block with its JSON as <see cref="ContentBlock.Raw"/> (MDL-05), the usage
/// of the call as one increment, and the stop reason.
/// </summary>
internal static class ClaudeReply
{
    public static async Task<IReadOnlyList<ModelEvent>> ReadAsync(IReadOnlyList<BetaRawMessageStreamEvent> events)
    {
        var message = await Replay(events).Aggregate().ConfigureAwait(false);
        var read = new List<ModelEvent>();
        foreach (var block in message.Content)
        {
            var raw = block.Json.GetRawText();
            var text = block.Json.GetProperty("type").GetString() == "text" ? block.Json.GetProperty("text").GetString() : null;
            read.Add(new BlockReceived(new ContentBlock(text, raw)));
        }

        read.Add(new UsageReceived(Usage(message.Usage)));
        read.Add(Stop(message));
        return read;
    }

    /// <summary>The stop reason, by its raw word, so a reason added later keeps its word as the detail.</summary>
    private static ModelStopped Stop(BetaMessage message)
    {
        var reason = message.StopReason?.Raw();
        return reason switch
        {
            "end_turn" => new ModelStopped(ModelStopReason.End),
            "tool_use" => new ModelStopped(ModelStopReason.ToolUse),
            "max_tokens" => new ModelStopped(ModelStopReason.MaxTokens),
            "model_context_window_exceeded" => new ModelStopped(ModelStopReason.ContextFull),
            "refusal" => new ModelStopped(ModelStopReason.Refusal, Category(message)),
            _ => new ModelStopped(ModelStopReason.Unknown, reason),
        };
    }

    private static string? Category(BetaMessage message) => message.StopDetails?.Category?.Raw();

    /// <summary>
    /// The call's tokens. When the call ran several iterations (such as a compaction before the reply), the totals count
    /// only the last one, so the iterations are added up instead (spike S02, recommendation 2).
    /// </summary>
    private static Usage Usage(BetaUsage usage)
    {
        if (usage.RawData.TryGetValue("iterations", out var iterations) && iterations.ValueKind == JsonValueKind.Array && iterations.GetArrayLength() > 0)
        {
            return iterations.EnumerateArray().Aggregate(default(Usage), (sum, iteration) => sum + new Usage(
                Tokens(iteration, "input_tokens"), Tokens(iteration, "output_tokens"),
                Tokens(iteration, "cache_read_input_tokens"), Tokens(iteration, "cache_creation_input_tokens")));
        }

        return new Usage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0);
    }

    private static long Tokens(JsonElement iteration, string name) =>
        iteration.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt64() : 0;

    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Replay(IReadOnlyList<BetaRawMessageStreamEvent> events)
    {
        foreach (var streamEvent in events)
        {
            yield return streamEvent;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
