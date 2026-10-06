namespace Sleepyshark.Officina;

/// <summary>Tokens used since the call's previous report: reports are increments.</summary>
public sealed record UsageReceived(Usage Usage) : ModelEvent;
