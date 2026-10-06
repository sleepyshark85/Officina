using System.Globalization;

namespace Sleepyshark.Officina;

/// <summary>What a run has used so far, against its budget.</summary>
internal sealed class Spending(Agent agent, Budget? budget)
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

    /// <summary>The result with its cost, counts and duration.</summary>
    public RunResult Report(RunResult result) =>
        result with { Cost = Cost, ModelCalls = ModelCalls, ToolCalls = ToolCalls, Duration = Elapsed };

    /// <summary>The most output tokens the next call may use, or null when the budget does not limit them.</summary>
    public int? OutputLimit()
    {
        long? limit = budget?.Tokens - Usage.Total;
        if (budget?.Cost is { } cost && agent.Model.Price is { Output: > 0 } price)
        {
            var affordable = (long)Math.Min(Math.Floor((cost - Cost) * 1_000_000m / price.Output), long.MaxValue);
            limit = limit is null ? affordable : Math.Min(limit.Value, affordable);
        }

        return limit is null ? null : (int)Math.Clamp(limit.Value, 0, int.MaxValue);
    }

    /// <summary>Which limit is reached, as a sentence; null when the run may call the model again.</summary>
    public string? Reached()
    {
        return budget is null ? null
            : ModelCalls >= budget.ModelCalls ? Say($"The model call budget is used up: {ModelCalls} of {budget.ModelCalls}.")
            : Elapsed >= budget.Time ? Say($"The time budget is used up: {Elapsed.TotalSeconds:0.#} s of {budget.Time.Value.TotalSeconds:0.#} s.")
            : Usage.Total >= budget.Tokens ? Say($"The token budget is used up: {Usage.Total:N0} of {budget.Tokens:N0} tokens.")
            : budget.Cost is { } cost && (Cost >= cost || OutputLimit() < 1) ? Say($"The cost budget is used up: ${Cost:0.######} of ${cost:0.######}.")
            : null;
    }

    private static string Say(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
