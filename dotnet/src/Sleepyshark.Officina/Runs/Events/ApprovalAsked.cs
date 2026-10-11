namespace Sleepyshark.Officina;

/// <summary>
/// The run is about to ask the approver whether <paramref name="Call"/> may run. It follows everything before it in the
/// stream, but may reach the host before or after the approver is asked.
/// </summary>
public sealed record ApprovalAsked(ToolCall Call) : RunEvent;
