using System.Diagnostics;

namespace Sleepyshark.Officina;

/// <summary>What the steps of one run share: its agent and conversation, its span, its audit trail, its spending and what its tools get.</summary>
internal sealed class RunScope : IDisposable
{
    public RunScope(AgentDefinition agent, Conversation conversation, Activity? span, RunOptions options)
    {
        Agent = agent;
        Conversation = conversation;
        Span = span;
        Audit = new AuditRecorder(agent, conversation, span, options.MemoryScope);
        Spending = new Spending(agent, options.Budget);
        Tools = new ToolContext(options.MemoryScope);
    }

    public AgentDefinition Agent { get; }

    public Conversation Conversation { get; }

    /// <summary>The run's span, if anything listens; the parent of its model and tool call spans.</summary>
    public Activity? Span { get; }

    public AuditRecorder Audit { get; }

    public Spending Spending { get; }

    /// <summary>What each of the run's tool handlers gets.</summary>
    public ToolContext Tools { get; }

    public void Dispose() => Audit.Dispose();
}
