using System.Globalization;

namespace Sleepyshark.Officina;

/// <summary>
/// Limits on one run (BUD-01), each optional. Every limit is checked before every model call, and the call's output token
/// limit is lowered to what the remaining cost and tokens allow, so a call overshoots by at most its input (about twice
/// that when it compacts, as compaction reads the prompt again). A call in flight is never stopped.
/// </summary>
public sealed record Budget
{
    /// <summary>The most the run may spend, in US dollars, at the model's price; it needs a model with a price.</summary>
    public decimal? Cost { get; init; }

    /// <summary>The most tokens the run may use, of every kind (input, output, cache reads and writes).</summary>
    public long? Tokens { get; init; }

    public int? ModelCalls { get; init; }

    /// <summary>The longest the run may take; checked before each model call, so a call that starts in time may end after it.</summary>
    public TimeSpan? Time { get; init; }
}

/// <summary>What a run has used so far, against its agent's budget (the budget guard of ARCHITECTURE §3).</summary>
internal sealed class Spending(AgentDefinition agent)
{
    private readonly long started = agent.Time.GetTimestamp();

    public Usage Usage { get; private set; }

    public decimal Cost { get; private set; }

    public int ModelCalls { get; private set; }

    public int ToolCalls { get; set; }

    public TimeSpan Elapsed => agent.Time.GetElapsedTime(started);

    /// <summary>Counts a model call that used <paramref name="usage"/>.</summary>
    public void AddCall(Usage usage)
    {
        Usage += usage;
        Cost += agent.Model.Price?.Cost(usage) ?? 0;
        ModelCalls++;
    }

    /// <summary>The result's usage figures beside its tokens (BUD-03).</summary>
    public RunResult Report(RunResult result) =>
        result with { Cost = Cost, ModelCalls = ModelCalls, ToolCalls = ToolCalls, Duration = Elapsed };

    /// <summary>The most output tokens the next call may use, or null when the budget does not limit them.</summary>
    public int? OutputLimit()
    {
        long? limit = agent.Budget?.Tokens - Usage.Total;
        if (agent.Budget?.Cost is { } cost && agent.Model.Price is { Output: > 0 } price)
        {
            var affordable = (long)Math.Min(Math.Floor((cost - Cost) * 1_000_000m / price.Output), long.MaxValue);
            limit = limit is null ? affordable : Math.Min(limit.Value, affordable);
        }

        return limit is null ? null : (int)Math.Clamp(limit.Value, 0, int.MaxValue);
    }

    /// <summary>Which limit is reached, as a sentence; null when the run may call the model again.</summary>
    public string? Reached()
    {
        var budget = agent.Budget;
        return budget is null ? null
            : ModelCalls >= budget.ModelCalls ? Say($"The model call budget is used up: {ModelCalls} of {budget.ModelCalls}.")
            : Elapsed >= budget.Time ? Say($"The time budget is used up: {Elapsed.TotalSeconds:0.#} s of {budget.Time.Value.TotalSeconds:0.#} s.")
            : Usage.Total >= budget.Tokens ? Say($"The token budget is used up: {Usage.Total:N0} of {budget.Tokens:N0} tokens.")
            : budget.Cost is { } cost && (Cost >= cost || OutputLimit() < 1) ? Say($"The cost budget is used up: ${Cost:0.######} of ${cost:0.######}.")
            : null;
    }

    private static string Say(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
