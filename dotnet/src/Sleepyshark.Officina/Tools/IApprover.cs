namespace Sleepyshark.Officina;

/// <summary>Answers approval requests: a person or a policy. A run without one is unattended.</summary>
public interface IApprover
{
    /// <summary>Decides whether <paramref name="toolCall"/> of <paramref name="tool"/>, which needs approval, may run.</summary>
    Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken);
}
