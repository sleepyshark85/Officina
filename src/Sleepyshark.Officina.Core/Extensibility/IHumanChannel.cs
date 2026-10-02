namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// How a human is reached for approvals, questions and sign-offs (HITL). Only the asking agent waits for the answer
/// (LOOP-12). The core applies the deadline: it cancels the wait when the deadline passes (HITL-02).
/// </summary>
public interface IHumanChannel
{
    ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct);
}
