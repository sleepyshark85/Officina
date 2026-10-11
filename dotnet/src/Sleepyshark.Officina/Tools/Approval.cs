namespace Sleepyshark.Officina;

/// <summary>An approver's answer; a denial's reason is told to the model.</summary>
public sealed record Approval(bool Approved, string? Reason = null)
{
    public static Approval Granted { get; } = new(true);

    public static Approval Denied(string reason) => new(false, reason);
}
