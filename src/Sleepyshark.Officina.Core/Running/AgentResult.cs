namespace Sleepyshark.Officina.Core.Running;

public enum AgentOutcome
{
    Completed,
    HandedOff,
    Rejected,
}

/// <summary>How a run ended. Every run ends in one of these, never a crash.</summary>
/// <param name="Outcome">Whether the run completed, was handed off or was rejected.</param>
/// <param name="Output">The agent's output when completed; otherwise the handoff or rejection reason.</param>
public sealed record AgentResult(AgentOutcome Outcome, string Output)
{
    public static AgentResult Completed(string output) => new(AgentOutcome.Completed, output);

    public static AgentResult HandedOff(string reason) => new(AgentOutcome.HandedOff, reason);

    public static AgentResult Rejected(string reason) => new(AgentOutcome.Rejected, reason);
}
