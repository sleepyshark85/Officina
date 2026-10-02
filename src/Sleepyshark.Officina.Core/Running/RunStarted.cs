using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// How a run started: the agent, the owner, and the exact configuration it used, defaults included, so its behaviour
/// can be reproduced and explained later (CFG-07). With the work it was given, a crashed run can start again (RUN-04).
/// </summary>
/// <param name="RunId">The run's id.</param>
/// <param name="Agent">The agent definition the run starts with.</param>
/// <param name="Owner">Who the run acts for, or null for an anonymous caller (PRIV-02).</param>
/// <param name="Time">When it started.</param>
/// <param name="CoreVersion">The core that ran it.</param>
/// <param name="Configuration">The resolved configuration. Durable storage writes it as text.</param>
public sealed record RunStarted(string RunId, string Agent, string? Owner, DateTimeOffset Time, string CoreVersion, OfficinaOptions Configuration)
{
    /// <summary>The work the run was given, as admission passed it (masked, as the run record and the history are).</summary>
    public string Input { get; init; } = "";

    /// <summary>How the work arrived.</summary>
    public Trigger Trigger { get; init; } = Trigger.Request;

    /// <summary>The task the work is for, if any.</summary>
    public string? TaskId { get; init; }
}

/// <summary>
/// Where a run is (RUN-01). Only its start and end are stored: it is running until it ends, and a run whose turn is handed
/// off waits for a human. Pausing the whole run arrives with the team (S20).
/// </summary>
public enum RunStatus
{
    Running,
    WaitingForHuman,
    Completed,
    Failed,
    Cancelled,
}
