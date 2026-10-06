namespace Sleepyshark.Officina;

/// <summary>The provider compacted the conversation before replying; the summary block is among the reply's blocks.</summary>
/// <param name="Tokens">The input tokens summarized.</param>
/// <param name="SummaryTokens">The summary's size, in tokens.</param>
public sealed record CompactionReported(long Tokens, long SummaryTokens) : ModelEvent;
