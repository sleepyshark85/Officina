namespace Sleepyshark.Officina;

/// <summary>A tool call got its result, as the model will see it; every call gets one.</summary>
public sealed record ToolCallFinished(ToolCall Call, ToolResult Result) : RunEvent;
