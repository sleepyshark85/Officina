namespace Sleepyshark.Officina;

/// <summary>The run began handling a tool call: its approval, if needed, then the tool.</summary>
public sealed record ToolCallStarted(ToolCall Call) : RunEvent;
