namespace Sleepyshark.Officina.Testing;

/// <summary>
/// The human at an approval prompt, scripted (TEST-01): answers each request with the next answer given in advance, in
/// order, and records the calls it was asked about. It throws when it has no answer left, which denies the call.
/// </summary>
public sealed class ScriptedApprover : IApprover
{
    private readonly Lock gate = new();
    private readonly Queue<Approval> answers = new();
    private readonly List<ToolCall> asked = [];

    /// <summary>The calls asked about so far, in order.</summary>
    public IReadOnlyList<ToolCall> Asked
    {
        get
        {
            lock (gate)
            {
                return [.. asked];
            }
        }
    }

    /// <summary>Adds answers, in order.</summary>
    public ScriptedApprover Answer(params Approval[] approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        lock (gate)
        {
            foreach (var approval in approvals)
            {
                answers.Enqueue(approval);
            }
        }

        return this;
    }

    public Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            asked.Add(toolCall);
            return answers.TryDequeue(out var answer)
                ? Task.FromResult(answer)
                : throw new InvalidOperationException($"The scripted approver was asked about call {asked.Count} but has no answer left.");
        }
    }
}
