namespace Sleepyshark.Officina;

/// <summary>How a run ended: exactly one of <see cref="Completed"/>, <see cref="Stopped"/> or <see cref="Failed"/>.</summary>
/// <param name="Usage">Tokens used by the run's model calls.</param>
public abstract record RunResult(Usage Usage)
{
    /// <summary>What the run's tokens cost, in US dollars; zero when the model has no price.</summary>
    public decimal Cost { get; init; }

    public int ModelCalls { get; init; }

    /// <summary>The tool calls the run handled, denied and failed ones included.</summary>
    public int ToolCalls { get; init; }

    public TimeSpan Duration { get; init; }
}
