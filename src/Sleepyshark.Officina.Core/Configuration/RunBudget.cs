namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The limits of one run (RUN-05). Each limit protects INV-07: it can be raised, never removed.</summary>
public sealed record RunBudget
{
    [Setting("The most the run may spend, in USD.", Example = "25", Minimum = 0, ExclusiveMinimum = true, Invariant = "INV-07", Live = true)]
    public decimal Cost { get; init; } = 25m;

    [Setting("The longest the run may take: a number with a unit, `ms`, `s`, `m`, `h` or `d`.",
        Example = "\"8h\"", Minimum = 0, ExclusiveMinimum = true, Invariant = "INV-07", Live = true)]
    public TimeSpan Time { get; init; } = TimeSpan.FromHours(8);
}
