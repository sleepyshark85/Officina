namespace Sleepyshark.Officina;

/// <summary>A message was appended. The run waits while the host handles it, so the host can save after every step.</summary>
public sealed record ConversationAppended(Conversation Conversation, Message Message) : RunEvent;
