namespace Sleepyshark.Officina;

/// <summary>What a model's provider supports beyond the basic contract.</summary>
[Flags]
public enum ModelCapabilities
{
    None = 0,

    /// <summary>Server-side compaction of the conversation into a summary block.</summary>
    Compaction = 1,

    /// <summary>Server-side clearing of old tool results.</summary>
    ContextEditing = 2,
}

/// <summary>
/// How the provider shortens a long conversation on its side; the core never edits it. Fixed per conversation and part
/// of the prefix.
/// </summary>
public sealed record ContextManagement
{
    /// <summary>Compacts once a request's input reaches this many tokens; null for none. Claude's minimum is 50,000.</summary>
    public long? CompactAt
    {
        get;
        init => field = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold must be positive.");
    }

    /// <summary>Clears old tool results; null for none.</summary>
    public ToolResultClearing? ClearToolResults { get; init; }
}

/// <summary>Clearing of old tool results: which go, and when.</summary>
/// <param name="After">Clears once the conversation holds more than this many tool calls.</param>
/// <param name="Keep">How many of the latest tool calls keep their results.</param>
/// <param name="AtLeastTokens">
/// Clears only when at least this many input tokens go. Each clearing rewrites the cached tail, so this keeps clearings
/// few and worth their cost; zero clears whatever there is.
/// </param>
public sealed record ToolResultClearing(int After, int Keep, long AtLeastTokens = 0)
{
    public int After { get; } = After > 0 ? After : throw new ArgumentOutOfRangeException(nameof(After), After, "The threshold must be positive.");

    public int Keep { get; } = Keep >= 0 ? Keep : throw new ArgumentOutOfRangeException(nameof(Keep), Keep, "Keep cannot be negative.");

    public long AtLeastTokens { get; } = AtLeastTokens >= 0 ? AtLeastTokens : throw new ArgumentOutOfRangeException(nameof(AtLeastTokens), AtLeastTokens, "The minimum cannot be negative.");
}

/// <summary>The provider compacted the conversation before replying; the summary block is among the reply's blocks.</summary>
/// <param name="Tokens">The input tokens summarized.</param>
/// <param name="SummaryTokens">The summary's size, in tokens.</param>
public sealed record CompactionReported(long Tokens, long SummaryTokens) : ModelEvent;

/// <summary>The provider cleared old tool results from this request's view of the conversation.</summary>
/// <param name="Tokens">The input tokens cleared.</param>
/// <param name="ToolCalls">How many tool calls had their results cleared.</param>
public sealed record ClearingReported(long Tokens, int ToolCalls) : ModelEvent;

/// <summary>The provider compacted the conversation during a model call; see <see cref="CompactionReported"/>.</summary>
public sealed record ConversationCompacted(long Tokens, long SummaryTokens) : RunEvent;

/// <summary>
/// The provider cleared old tool results for a model call; see <see cref="ClearingReported"/>. The conversation keeps
/// them, so the provider clears and reports them again on every later call.
/// </summary>
public sealed record ToolResultsCleared(long Tokens, int ToolCalls) : RunEvent;
