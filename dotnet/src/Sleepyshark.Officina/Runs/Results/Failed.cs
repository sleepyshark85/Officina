namespace Sleepyshark.Officina;

/// <summary>The run could not do its work; <paramref name="Error"/> says why.</summary>
public sealed record Failed(FailureReason Reason, string Error, Usage Usage) : RunResult(Usage);
