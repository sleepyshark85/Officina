namespace Sleepyshark.Officina;

/// <summary>The run ended early for <paramref name="Reason"/>; <paramref name="Detail"/> is a refusal's category, if any.</summary>
public sealed record Stopped(StopReason Reason, string? Detail, Usage Usage) : RunResult(Usage);
