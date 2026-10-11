namespace Sleepyshark.Officina;

/// <summary>What an audit entry records.</summary>
public enum AuditKind
{
    RunStarted,

    /// <summary>The run's result, refusals, provider failures and prefix mismatches included, with its usage.</summary>
    RunEnded,

    /// <summary>A tool call is about to run; a write runs only once this is recorded.</summary>
    ToolStarted,

    /// <summary>A tool call's outcome: every call has one, whether it ran or not.</summary>
    ToolEnded,

    ApprovalAsked,

    ApprovalAnswered,

    /// <summary>
    /// A tool source connected, failed to connect, or lost its connection; the entry's tool names the source. With a shared
    /// source, it is recorded by the run that noticed it, not always the one whose call met it.
    /// </summary>
    ToolSource,

    /// <summary>The provider compacted the conversation during a model call; the detail says how much.</summary>
    Compacted,

    /// <summary>The provider cleared old tool results for a model call; the detail says how many.</summary>
    Cleared,
}
