using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>Who makes a batch of tool calls: the run, the agent and the caller it acts for. The host sets it, never the model (INV-03).</summary>
public sealed record ToolContext(string RunId, string Agent, Caller Caller)
{
    /// <summary>The task the agent works on, if any, from the work (TASK).</summary>
    public string? TaskId { get; init; }

    /// <summary>The run's masking, when it is on (ING-06).</summary>
    internal Masker? Masker { get; init; }

    /// <summary>The step of the agent's pattern, as a path such as <c>fix/review</c>; null for the agent's own turn (EVT-02).</summary>
    internal string? Step { get; init; }

    /// <summary>Whether the agent has read untrusted content (SEC-04). Once set, it stays set.</summary>
    internal bool ReadUntrusted { get; set; }
}
