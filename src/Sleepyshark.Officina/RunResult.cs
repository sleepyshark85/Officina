namespace Sleepyshark.Officina;

/// <summary>How a run ended (AGT-03): exactly one of <see cref="Completed"/>, <see cref="Stopped"/> or <see cref="Failed"/>.</summary>
/// <param name="Usage">Tokens used by the run's model calls.</param>
public abstract record RunResult(Usage Usage);

/// <summary>The model finished; <paramref name="Text"/> is its final reply's text, with the agent's secrets redacted (EVT-03).</summary>
public sealed record Completed(string Text, Usage Usage) : RunResult(Usage);

/// <summary>The run ended early for <paramref name="Reason"/>; <paramref name="Detail"/> is a refusal's category, when the provider gives one.</summary>
public sealed record Stopped(StopReason Reason, string? Detail, Usage Usage) : RunResult(Usage);

/// <summary>The run could not do its work; <paramref name="Error"/> says why.</summary>
public sealed record Failed(FailureReason Reason, string Error, Usage Usage) : RunResult(Usage);

/// <summary>Why a run stopped.</summary>
public enum StopReason
{
    /// <summary>The host cancelled the run (AGT-05).</summary>
    Cancelled,

    /// <summary>The model declined the request (MDL-06).</summary>
    Refusal,

    /// <summary>
    /// The reply reached the output token limit. A reply that requested tools but stopped for any reason other than tool
    /// use is not appended, nor are the user message and run context it answers, and its tools do not run: its last tool
    /// input may be cut short, and its calls would be left without results.
    /// </summary>
    OutputLimit,

    /// <summary>The conversation no longer fits the model's context window.</summary>
    ContextFull,

    /// <summary>The run made as many model calls as one run may, and the model still asked for tools; their results are kept.</summary>
    IterationLimit,
}

/// <summary>Why a run failed.</summary>
public enum FailureReason
{
    /// <summary>The model call failed after the provider's retries, or its reply was malformed.</summary>
    ModelError,

    /// <summary>The model stopped for a reason the run cannot act on.</summary>
    UnexpectedStop,

    /// <summary>The agent's tools, instructions or model settings differ from those the conversation was started with (CTX-04).</summary>
    PrefixMismatch,
}
