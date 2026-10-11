namespace Sleepyshark.Officina;

/// <summary>The model call was retried after its reply had started: discard the text streamed so far.</summary>
public sealed record ReplyRestarted : RunEvent;
