namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// How work reaches an agent (TRG-01). The host delivers it; the agent behaves the same whichever it is (TRG-02).
/// Conversations keep their history with S07, and long-running runs resume with S19.
/// </summary>
public enum Trigger
{
    Conversation,
    Request,
    Batch,
    Schedule,
    Event,
    LongRunning,
}
