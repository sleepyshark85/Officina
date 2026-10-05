namespace Sleepyshark.Officina;

/// <summary>What a model's provider supports beyond the basic contract (ARCHITECTURE §4.1).</summary>
[Flags]
public enum ModelCapabilities
{
    None = 0,

    /// <summary>Server-side compaction of the conversation into a summary block (HIST-01).</summary>
    Compaction = 1,

    /// <summary>Server-side clearing of old tool results (HIST-02).</summary>
    ContextEditing = 2,
}

/// <summary>
/// How the provider shortens a long conversation on its side (HIST-01, HIST-02); the core never edits the conversation
/// itself (HIST-03). Fixed per conversation, as it shapes every request: it is part of the prefix fingerprint (CTX-04).
/// </summary>
public sealed record ContextManagement
{
    /// <summary>
    /// Compacts the conversation once a request's input reaches this many tokens (HIST-01); null for no compaction. The
    /// provider sets a minimum (Claude: 50,000).
    /// </summary>
    public long? CompactAt
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold must be positive.");
    }

    /// <summary>Clears old tool results (HIST-02); null for no clearing.</summary>
    public ToolResultClearing? ClearToolResults { get; init; }
}

/// <summary>Clearing of old tool results (HIST-02): which results go, and when.</summary>
/// <param name="After">Clears once the conversation holds more than this many tool calls.</param>
/// <param name="Keep">How many of the latest tool calls keep their results.</param>
/// <param name="AtLeastTokens">
/// Clears only when at least this many input tokens go. Each clearing rewrites the cached tail of the conversation, so
/// this keeps clearings few and worth their cost; zero clears whatever there is.
/// </param>
public sealed record ToolResultClearing(int After, int Keep, long AtLeastTokens = 0)
{
    public int After { get; } = After > 0 ? After : throw new ArgumentOutOfRangeException(nameof(After), After, "The threshold must be positive.");

    public int Keep { get; } = Keep >= 0 ? Keep : throw new ArgumentOutOfRangeException(nameof(Keep), Keep, "Keep cannot be negative.");

    public long AtLeastTokens { get; } = AtLeastTokens >= 0 ? AtLeastTokens : throw new ArgumentOutOfRangeException(nameof(AtLeastTokens), AtLeastTokens, "The minimum cannot be negative.");
}

/// <summary>The provider compacted the conversation before replying (HIST-04); the summary block is among the reply's blocks.</summary>
/// <param name="Tokens">The input tokens summarized.</param>
/// <param name="SummaryTokens">The summary's size, in tokens.</param>
public sealed record CompactionReported(long Tokens, long SummaryTokens) : ModelEvent;

/// <summary>The provider cleared old tool results from this request's view of the conversation (HIST-04).</summary>
/// <param name="Tokens">The input tokens cleared.</param>
/// <param name="ToolCalls">How many tool calls had their results cleared.</param>
public sealed record ClearingReported(long Tokens, int ToolCalls) : ModelEvent;

/// <summary>The provider compacted the conversation during a model call (HIST-04); see <see cref="CompactionReported"/>.</summary>
public sealed record ConversationCompacted(long Tokens, long SummaryTokens) : RunEvent;

/// <summary>
/// The provider cleared old tool results for a model call (HIST-04); see <see cref="ClearingReported"/>. The conversation
/// itself keeps them, so the provider clears them again on every later call, and reports it each time.
/// </summary>
public sealed record ToolResultsCleared(long Tokens, int ToolCalls) : RunEvent;
