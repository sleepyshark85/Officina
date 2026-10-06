namespace Sleepyshark.Officina;

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
