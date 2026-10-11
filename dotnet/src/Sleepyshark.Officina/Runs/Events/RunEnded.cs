namespace Sleepyshark.Officina;

/// <summary>The run ended; always the last event.</summary>
public sealed record RunEnded(RunResult Result) : RunEvent;
