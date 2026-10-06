namespace Sleepyshark.Officina;

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
