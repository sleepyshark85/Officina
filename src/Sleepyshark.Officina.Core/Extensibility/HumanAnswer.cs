namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>A human's answer to an approval. The human interaction slice (S16) adds approving a changed version.</summary>
public sealed record HumanAnswer(bool Approved)
{
    public static HumanAnswer Approve { get; } = new(true);

    public static HumanAnswer Deny { get; } = new(false);
}
