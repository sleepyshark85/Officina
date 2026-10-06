namespace Sleepyshark.Officina;

/// <summary>
/// Limits on one run, each optional, checked before every model call. Each call's output limit is lowered to what the
/// remaining cost and tokens allow, so a call overshoots by at most its input (about twice that when it compacts). A
/// call in flight is never stopped.
/// </summary>
public sealed record Budget
{
    /// <summary>The most the run may spend, in US dollars at the model's price; needs a model with a price.</summary>
    public decimal? Cost { get; init; }

    /// <summary>The most tokens the run may use: input, output, cache reads and writes.</summary>
    public long? Tokens { get; init; }

    public int? ModelCalls { get; init; }

    /// <summary>The longest the run may take; a call that starts in time may end after it.</summary>
    public TimeSpan? Time { get; init; }
}
