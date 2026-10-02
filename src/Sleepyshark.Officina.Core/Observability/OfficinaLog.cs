using System.Diagnostics.Tracing;

namespace Sleepyshark.Officina.Core.Observability;

/// <summary>
/// The structured log (OBS-03), as an event source the host forwards to its logging, for example with OpenTelemetry's
/// event source bridge. Each entry names its run and agent, and its step when the agent's pattern has steps. No entry holds conversation content: only names, categories and internal error details
/// with known secrets removed.
/// </summary>
[EventSource(Name = SourceName)]
internal sealed class OfficinaLog : EventSource
{
    public const string SourceName = "Sleepyshark-Officina";

    public static readonly OfficinaLog Log = new();

    private OfficinaLog()
    {
    }

    /// <param name="runId">The run.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="outcome">How the turn ended.</param>
    /// <param name="reason">The handoff reason, or the type of the exception a failed turn ended with; empty otherwise.</param>
    [Event(1, Level = EventLevel.Informational, Message = "Run {0}: agent {1}'s turn ended: {2} {3}")]
    public void TurnEnded(string runId, string agent, string outcome, string reason) => WriteEvent(1, runId, agent, outcome, reason);

    /// <summary>A tool call failed. The detail is what the model is never told, such as an exception's message (TOOL-08).</summary>
    /// <param name="runId">The run.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="step">The step of the agent's pattern; empty for the agent's own turn.</param>
    /// <param name="tool">The tool.</param>
    /// <param name="category">The error category the model was told.</param>
    /// <param name="detail">The internal detail, with known secrets removed.</param>
    [Event(2, Level = EventLevel.Warning, Message = "Run {0}: agent {1}'s call of {3} in step {2} failed ({4}): {5}")]
    public void ToolFailed(string runId, string agent, string step, string tool, string category, string detail) =>
        WriteEvent(2, runId, agent, step, tool, category, detail);

    /// <param name="runId">The run.</param>
    /// <param name="agent">The agent.</param>
    /// <param name="step">The step of the agent's pattern.</param>
    /// <param name="outcome">How the step ended.</param>
    [Event(3, Level = EventLevel.Informational, Message = "Run {0}: agent {1}'s step {2} ended: {3}")]
    public void StepEnded(string runId, string agent, string step, string outcome) => WriteEvent(3, runId, agent, step, outcome);
}
