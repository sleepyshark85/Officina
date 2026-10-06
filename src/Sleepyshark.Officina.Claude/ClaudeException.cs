namespace Sleepyshark.Officina.Claude;

/// <summary>A Claude call that failed, with its kind; the run fails with its message.</summary>
public sealed class ClaudeException(ClaudeFailure failure, string message, Exception inner) : Exception(message, inner)
{
    public ClaudeFailure Failure { get; } = failure;
}
