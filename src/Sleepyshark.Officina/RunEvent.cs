namespace Sleepyshark.Officina;

/// <summary>Something that happened during a run, streamed to the host as it happens (EVT-01).</summary>
public abstract record RunEvent;

/// <summary>A piece of the model's reply text.</summary>
public sealed record TextStreamed(string Text) : RunEvent;

/// <summary>The model call was retried after its reply had started: discard the text streamed so far, as the reply starts again.</summary>
public sealed record ReplyRestarted : RunEvent;

/// <summary>Tokens a model call reported.</summary>
public sealed record UsageReported(Usage Usage) : RunEvent;

/// <summary>
/// A message was appended to the conversation (AGT-08). The run waits while the host handles the event, so the host can
/// persist the conversation after every step.
/// </summary>
public sealed record ConversationAppended(Conversation Conversation, Message Message) : RunEvent;

/// <summary>The run ended; always the last event.</summary>
public sealed record RunEnded(RunResult Result) : RunEvent;
