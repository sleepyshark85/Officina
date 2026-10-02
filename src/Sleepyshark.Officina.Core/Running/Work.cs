using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>Work from outside the core, as the host delivers it to an agent (TRG-01). It passes admission first (ING-01).</summary>
/// <param name="Agent">The agent, by its name in <c>agents</c>.</param>
/// <param name="Input">What the agent is asked to do.</param>
public sealed record Work(string Agent, string Input)
{
    /// <summary>How the work arrived.</summary>
    public Trigger Trigger { get; init; } = Trigger.Request;

    /// <summary>Who the run acts for; an anonymous caller by default.</summary>
    public Caller Caller { get; init; } = Caller.Anonymous;

    /// <summary>
    /// Whether the requester asks for a human: the run hands off to one without calling the model (EGR-04).
    /// </summary>
    public bool HandOffToHuman { get; init; }

    /// <summary>The task on the run's task board that the work is for, if any.</summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// The id of the run the work starts, known before it starts, so a reader can follow the run's events from its start.
    /// A copy made with <c>with</c> keeps it, so make a new work item for new work. A run that starts again after a crash
    /// keeps its own id (RUN-04).
    /// </summary>
    public string RunId { get; init; } = Guid.CreateVersion7().ToString();
}
