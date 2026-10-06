using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>The session in use: its conversation, who started it, what its replies used, and its last run context.</summary>
public sealed class Session(Conversation conversation, string staffMember)
{
    public Conversation Conversation { get; } = conversation;

    public string Id => Conversation.Id;

    public string StaffMember { get; } = staffMember;

    public Usage Usage { get; set; }

    public decimal Cost { get; set; }

    public string? Context { get; set; }

    /// <summary>How many tool calls the last clearing line named, so a clearing the provider repeats is shown once.</summary>
    public int? ClearedToolCalls { get; set; }

    /// <summary>The text the session was last stored as; null before its first save.</summary>
    public string? Saved { get; set; }

    /// <summary>Whether the conversation grew since this console took it up, so leaving it needs a new summary.</summary>
    public bool Changed { get; set; }
}
