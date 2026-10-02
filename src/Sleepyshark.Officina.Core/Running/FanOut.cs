using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// Branches run in parallel, at most <c>maxParallel</c> at once: different steps on the input, or one step on each
/// item of a list in it. Their results are combined as configured (PAT-01, PAT-05).
/// </summary>
internal sealed class FanOut : ILoopPattern
{
    public async ValueTask<StepResult> RunAsync(PatternContext context, CancellationToken ct)
    {
        var pattern = context.Pattern;
        List<(StepOptions Step, string Input)> branches;
        if (pattern.Over is { } over)
        {
            if (Items(context.Input, over) is not { } items)
            {
                return context.HandOff(HandoffReason.InvalidStructuredOutput, $"the input has no list at {over}");
            }

            branches = [.. items.Select(item => (pattern.Branches[0], item))];
        }
        else
        {
            branches = [.. pattern.Branches.Select(step => (step, context.Input))];
        }

        var results = new StepResult[branches.Count];
        StepResult? first = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = pattern.MaxParallel, CancellationToken = stop.Token };
            await Parallel.ForEachAsync(Enumerable.Range(0, branches.Count), parallel, async (index, branchCt) =>
            {
                results[index] = await context.RunStepAsync($"branch[{index}]", branches[index].Step, branches[index].Input, branchCt).ConfigureAwait(false);
                if (pattern.Combine == FanOutCombine.FirstSuccess && results[index].Outcome == StepOutcome.Completed
                    && Interlocked.CompareExchange(ref first, results[index], null) is null)
                {
                    await stop.CancelAsync().ConfigureAwait(false); // the others are not needed
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (first is not null && !ct.IsCancellationRequested)
        {
        }

        var unfinished = results.FirstOrDefault(result => result?.Outcome != StepOutcome.Completed);
        return pattern.Combine switch
        {
            FanOutCombine.FirstSuccess => first ?? unfinished!,
            _ when unfinished is not null => unfinished,
            FanOutCombine.Majority => Majority(context, results),
            FanOutCombine.Step => await context.RunStepAsync(
                "combine", pattern.Combiner!, Labels.Parts([.. results.Select((result, index) => ($"step:branch[{index}]", result.Output))]), ct).ConfigureAwait(false),
            _ => new(StepOutcome.Completed, JsonSerializer.Serialize(results.Select(result => result.Output))),
        };
    }

    /// <summary>The items of the list at a path in the input, each as text; null when the input is not JSON or has no list there.</summary>
    private static List<string>? Items(string input, string path)
    {
        try
        {
            return Condition.Read(JsonElement.Parse(input), path) is { ValueKind: JsonValueKind.Array } list ? [.. list.EnumerateArray().Select(Condition.Text)] : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first branch whose value of <c>on</c> more than half of the branches share; a handoff when there is none.</summary>
    private static StepResult Majority(PatternContext context, StepResult[] results)
    {
        var on = context.Pattern.On!;
        var votes = results.Select(result => Condition.Read(JsonElement.Parse(result.Output), on) is { } value ? value.GetRawText() : null).ToList();
        var winner = votes.Where(vote => vote is not null).GroupBy(vote => vote).FirstOrDefault(group => group.Count() * 2 > results.Length)?.Key;
        return winner is null
            ? context.HandOff(HandoffReason.NoRouteForValue, $"the branches reach no majority on {on}")
            : results[votes.IndexOf(winner)];
    }
}
