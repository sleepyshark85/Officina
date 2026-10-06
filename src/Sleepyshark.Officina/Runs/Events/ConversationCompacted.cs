namespace Sleepyshark.Officina;

/// <summary>The provider compacted the conversation during a model call; see <see cref="CompactionReported"/>.</summary>
public sealed record ConversationCompacted(long Tokens, long SummaryTokens) : RunEvent;
