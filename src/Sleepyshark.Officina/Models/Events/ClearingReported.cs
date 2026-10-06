namespace Sleepyshark.Officina;

/// <summary>The provider cleared old tool results from this request's view of the conversation.</summary>
/// <param name="Tokens">The input tokens cleared.</param>
/// <param name="ToolCalls">How many tool calls had their results cleared.</param>
public sealed record ClearingReported(long Tokens, int ToolCalls) : ModelEvent;
