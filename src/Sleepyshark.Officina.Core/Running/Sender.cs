namespace Sleepyshark.Officina.Core.Running;

public enum SenderKind
{
    Owner,
    Operator,
    Agent,
}

/// <summary>
/// Who sent a message to an agent (MSG-04). The owner and operators may give instructions; another agent's message is
/// data (INV-08).
/// </summary>
/// <param name="Kind">Whether the owner, an operator or another agent sent it.</param>
/// <param name="Agent">The sending agent's name, for <see cref="SenderKind.Agent"/>.</param>
public sealed record Sender(SenderKind Kind, string? Agent = null)
{
    public static Sender Owner { get; } = new(SenderKind.Owner);

    public static Sender Operator { get; } = new(SenderKind.Operator);
}
