namespace Sleepyshark.Officina;

/// <summary>The model finished; <paramref name="Text"/> is its final reply's text, secrets redacted.</summary>
public sealed record Completed(string Text, Usage Usage) : RunResult(Usage)
{
    /// <summary>The reply as the agent's output type, when it requires typed output; otherwise null.</summary>
    public object? Output { get; init; }
}
