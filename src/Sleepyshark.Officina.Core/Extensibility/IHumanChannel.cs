namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>How a human is reached for approvals (HITL). The human interaction slice (S16) adds questions, sign-offs and timeouts.</summary>
public interface IHumanChannel
{
    ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct);
}
