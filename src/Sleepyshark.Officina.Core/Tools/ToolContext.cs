using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>Who makes a batch of tool calls: the run, the agent and the caller it acts for. The host sets it, never the model (INV-03).</summary>
/// <param name="RunId">The run.</param>
/// <param name="Agent">The agent's definition, by its name in <c>agents</c>.</param>
/// <param name="Caller">Who the run acts for.</param>
public sealed record ToolContext(string RunId, string Agent, Caller Caller)
{
    /// <summary>The task the agent works on, if any, from the work (TASK).</summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// The agent's instance in its team, such as <c>developer[2]</c>, which the team gives it; null for an agent that is not in a
    /// team. One definition can have several instances at once, and each is an agent of its own (TEAM-01, TEAM-04).
    /// </summary>
    public string? Instance { get; init; }

    /// <summary>
    /// Whether the agent is its team's lead. Only the team sets it, so the lead's authority over the board and memory is this
    /// flag, never an agent's name (TEAM-02).
    /// </summary>
    public bool Lead { get; init; }

    /// <summary>Who the agent is in the run: its instance in a team, or else its definition's name.</summary>
    public string AgentId => Instance ?? Agent;

    /// <summary>The run's masking, when it is on (ING-06).</summary>
    internal Masker? Masker { get; init; }

    /// <summary>The step of the agent's pattern, as a path such as <c>fix/review</c>; null for the agent's own turn (EVT-02).</summary>
    internal string? Step { get; init; }

    /// <summary>
    /// Whether the agent has read untrusted content (SEC-04). Once set, it stays set. Within a run it is the run's: content flows
    /// between the run's steps and agents, through their outputs, the board and messages, so once any of them has read untrusted
    /// content, every one of them is marked at once, also those already working.
    /// </summary>
    internal bool ReadUntrusted => Run?.Set ?? own;

    /// <summary>The run's mark, which every context made from the run's shares; null outside a run, where the context has its own.</summary>
    internal UntrustedMark? Run { get; init; }

    private volatile bool own;

    /// <summary>Marks the agent, and so its run, as having read untrusted content (SEC-04).</summary>
    internal void MarkUntrusted()
    {
        if (Run is { } run)
        {
            run.Set = true;
        }
        else
        {
            own = true;
        }
    }

    /// <summary>The definition an agent's id names: an instance's, such as <c>developer</c> for <c>developer[2]</c>, or the id itself.</summary>
    public static string DefinitionOf(string agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        return agentId.IndexOf('[', StringComparison.Ordinal) is > 0 and var at ? agentId[..at] : agentId;
    }
}

/// <summary>Whether untrusted content has been read (SEC-04): only ever set, and read by every gate check as it is now.</summary>
internal sealed class UntrustedMark
{
    private volatile bool set;

    public bool Set
    {
        get => set;
        set
        {
            if (value)
            {
                this.set = true;
            }
        }
    }
}
