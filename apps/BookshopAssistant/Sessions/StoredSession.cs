using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>A stored session: its conversation, who started it, what its replies used, and the text it was stored as.</summary>
public sealed record StoredSession(Conversation Conversation, string StaffMember, Usage Usage, decimal Cost, string Saved);
