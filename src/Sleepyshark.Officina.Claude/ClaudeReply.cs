using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Reads a complete streamed reply: each content block with its JSON as <see cref="ContentBlock.Raw"/>, what the
/// provider did to shorten the conversation, the call's usage as one increment, and the stop reason.
/// </summary>
internal static class ClaudeReply
{
    private const string ToolInput = "input";

    public static async Task<IReadOnlyList<ModelEvent>> ReadAsync(IReadOnlyList<BetaRawMessageStreamEvent> events)
    {
        var message = await Replay(events).Aggregate().ConfigureAwait(false);
        var read = new List<ModelEvent>();
        foreach (var block in message.Content)
        {
            var text = block.TryPickText(out var said) ? said.Text : null;
            // The input stays as received: re-serializing it would escape its non-ASCII text.
            var call = block.TryPickToolUse(out var use) ? new ToolCall(use.ID, use.Name, block.Json.GetProperty(ToolInput).GetRawText()) : null;
            read.Add(new BlockReceived(new ContentBlock(text, block.Json.GetRawText(), call)));
        }

        read.AddRange(ContextEdits(message));
        read.Add(new UsageReceived(Usage(message.Usage)));
        read.Add(Stop(message));
        return read;
    }

    /// <summary>
    /// A compaction comes from the call's compaction iteration: what it read was summarized, what it wrote is the summary.
    /// A clearing comes from the edits the API applied, which do not include compaction.
    /// </summary>
    private static IEnumerable<ModelEvent> ContextEdits(BetaMessage message)
    {
        foreach (var iteration in message.Usage.Iterations ?? [])
        {
            if (iteration.TryPickBetaCompactionIterationUsage(out var compaction))
            {
                yield return new CompactionReported(
                    compaction.InputTokens + compaction.CacheReadInputTokens + compaction.CacheCreationInputTokens, compaction.OutputTokens);
            }
        }

        foreach (var edit in message.ContextManagement?.AppliedEdits ?? [])
        {
            if (edit.TryPickBetaClearToolUses20250919EditResponse(out var clearing))
            {
                yield return new ClearingReported(clearing.ClearedInputTokens, (int)clearing.ClearedToolUses);
            }
        }
    }

    /// <summary>The stop reason; one the SDK does not know keeps its raw word as the detail.</summary>
    private static ModelStopped Stop(BetaMessage message) => message.StopReason?.Value() switch
    {
        BetaStopReason.EndTurn => new ModelStopped(ModelStopReason.End),
        BetaStopReason.ToolUse => new ModelStopped(ModelStopReason.ToolUse),
        BetaStopReason.MaxTokens => new ModelStopped(ModelStopReason.MaxTokens),
        BetaStopReason.ModelContextWindowExceeded => new ModelStopped(ModelStopReason.ContextFull),
        BetaStopReason.Refusal => new ModelStopped(ModelStopReason.Refusal, message.StopDetails?.Category?.Raw()),
        _ => new ModelStopped(ModelStopReason.Unknown, message.StopReason?.Raw()),
    };

    /// <summary>
    /// The call's tokens, with hour-long cache writes apart, as they cost more. When a call runs several iterations (such as
    /// a compaction, then the reply), the totals count only the last, so the iterations are added up instead.
    /// </summary>
    private static Usage Usage(BetaUsage usage) => usage.Iterations is { Count: > 0 } iterations
        ? iterations.Aggregate(default(Usage), (sum, iteration) => sum + Usage(iteration))
        : new Usage(
            usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens ?? 0, usage.CacheCreationInputTokens ?? 0,
            usage.CacheCreation?.Ephemeral1hInputTokens ?? 0);

    /// <summary>One iteration's tokens; an iteration of a kind the SDK does not know counts none.</summary>
    private static Usage Usage(BetaUsageIteration iteration) =>
        iteration.TryPickBetaMessageIterationUsage(out var reply)
            ? new Usage(reply.InputTokens, reply.OutputTokens, reply.CacheReadInputTokens, reply.CacheCreationInputTokens, reply.CacheCreation?.Ephemeral1hInputTokens ?? 0)
        : iteration.TryPickBetaCompactionIterationUsage(out var compaction)
            ? new Usage(compaction.InputTokens, compaction.OutputTokens, compaction.CacheReadInputTokens, compaction.CacheCreationInputTokens, compaction.CacheCreation?.Ephemeral1hInputTokens ?? 0)
        : iteration.TryPickBetaAdvisorMessageIterationUsage(out var advisor)
            ? new Usage(advisor.InputTokens, advisor.OutputTokens, advisor.CacheReadInputTokens, advisor.CacheCreationInputTokens, advisor.CacheCreation?.Ephemeral1hInputTokens ?? 0)
        : iteration.TryPickBetaFallbackMessageIterationUsage(out var fallback)
            ? new Usage(fallback.InputTokens, fallback.OutputTokens, fallback.CacheReadInputTokens, fallback.CacheCreationInputTokens, fallback.CacheCreation?.Ephemeral1hInputTokens ?? 0)
        : default;

    /// <summary>
    /// The tokens an attempt reported before it failed mid-stream: its start's usage, updated by later deltas, whose counts
    /// are running totals. Zero when the attempt failed before it started.
    /// </summary>
    public static Usage Partial(IEnumerable<BetaRawMessageStreamEvent> events)
    {
        var usage = default(Usage);
        foreach (var streamEvent in events)
        {
            if (streamEvent.TryPickStart(out var start))
            {
                usage = Usage(start.Message.Usage);
            }
            else if (streamEvent.TryPickDelta(out var delta))
            {
                var counts = delta.Usage;
                usage = new Usage(
                    counts.InputTokens ?? usage.Input, counts.OutputTokens, counts.CacheReadInputTokens ?? usage.CacheRead,
                    counts.CacheCreationInputTokens ?? usage.CacheWrite, usage.CacheWriteHour);
            }
        }

        return usage;
    }

    private static async IAsyncEnumerable<BetaRawMessageStreamEvent> Replay(IReadOnlyList<BetaRawMessageStreamEvent> events)
    {
        foreach (var streamEvent in events)
        {
            yield return streamEvent;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}
