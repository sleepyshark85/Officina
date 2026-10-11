namespace Sleepyshark.Officina;

/// <summary>Tokens a model call reported, and their cost in US dollars (zero without a price).</summary>
public sealed record UsageReported(Usage Usage, decimal Cost) : RunEvent;
