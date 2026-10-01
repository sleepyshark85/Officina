namespace Sleepyshark.Officina.Core.Messages;

public enum Role
{
    User,
    Assistant,

    /// <summary>A system message in the middle of a conversation: an operator's message, or the volatile context.</summary>
    System,
}
