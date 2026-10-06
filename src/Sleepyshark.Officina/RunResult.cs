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

/// <summary>The model finished; <paramref name="Text"/> is its final reply's text, secrets redacted.</summary>
public sealed record Completed(string Text, Usage Usage) : RunResult(Usage)
{
    /// <summary>The reply as the agent's output type, when it requires typed output; otherwise null.</summary>
    public object? Output { get; init; }
}

/// <summary>The run ended early for <paramref name="Reason"/>; <paramref name="Detail"/> is a refusal's category, if any.</summary>
public sealed record Stopped(StopReason Reason, string? Detail, Usage Usage) : RunResult(Usage);

/// <summary>The run could not do its work; <paramref name="Error"/> says why.</summary>
public sealed record Failed(FailureReason Reason, string Error, Usage Usage) : RunResult(Usage);

/// <summary>Why a run stopped.</summary>
public enum StopReason
{
    /// <summary>The host cancelled the run.</summary>
    Cancelled,

    /// <summary>The model declined the request.</summary>
    Refusal,

    /// <summary>
    /// The reply reached the output token limit. A reply that asked for tools but stopped for another reason is not
    /// appended, nor are the message and context it answers, and its tools do not run: its last input may be cut short.
    /// </summary>
    OutputLimit,

    /// <summary>The conversation no longer fits the model's context window.</summary>
    ContextFull,

    /// <summary>A budget limit was reached before a model call, or cut the reply short; the detail says which.</summary>
    Budget,

    /// <summary>The run made as many model calls as a run may, and the model still asked for tools; their results are kept.</summary>
    IterationLimit,
}

/// <summary>Why a run failed.</summary>
public enum FailureReason
{
    /// <summary>The model call failed after retries, or its reply was malformed.</summary>
    ModelError,

    /// <summary>The model stopped for a reason the run cannot act on.</summary>
    UnexpectedStop,

    /// <summary>The agent's prefix differs from the one the conversation was started with.</summary>
    PrefixMismatch,

    /// <summary>The reply does not match the output schema, or does not deserialize into the output type.</summary>
    InvalidOutput,

    /// <summary>A tool source, such as an MCP server, could not be connected at the start of the run.</summary>
    ToolSourceUnavailable,
}
