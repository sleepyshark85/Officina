namespace Sleepyshark.Officina;

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
