namespace Sleepyshark.Officina;

/// <summary>The approver answered, or failed to, which denies the call.</summary>
public sealed record ApprovalAnswered(ToolCall Call, bool Approved) : RunEvent;
