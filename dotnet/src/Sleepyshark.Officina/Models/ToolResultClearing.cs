namespace Sleepyshark.Officina;

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
