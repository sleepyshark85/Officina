namespace Sleepyshark.Officina;

/// <summary>
/// The provider cleared old tool results for a model call; see <see cref="ClearingReported"/>. The conversation keeps
/// them, so the provider clears and reports them again on every later call.
/// </summary>
public sealed record ToolResultsCleared(long Tokens, int ToolCalls) : RunEvent;
