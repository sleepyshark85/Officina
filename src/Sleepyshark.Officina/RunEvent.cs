namespace Sleepyshark.Officina;

/// <summary>Something that happened during a run, streamed to the host as it happens (EVT-01).</summary>
public abstract record RunEvent;

/// <summary>A piece of the model's reply text.</summary>
public sealed record TextStreamed(string Text) : RunEvent;

/// <summary>The model call was retried after its reply had started: discard the text streamed so far, as the reply starts again.</summary>
public sealed record ReplyRestarted : RunEvent;

/// <summary>Tokens a model call reported, and what they cost in US dollars (zero when the model has no price).</summary>
public sealed record UsageReported(Usage Usage, decimal Cost) : RunEvent;

/// <summary>The run began handling a tool call the model requested: its approval, if needed, and then the tool.</summary>
public sealed record ToolCallStarted(ToolCall Call) : RunEvent;

/// <summary>A tool call got its result, as the model will get it; every requested call gets one (CTX-06).</summary>
public sealed record ToolCallFinished(ToolCall Call, ToolResult Result) : RunEvent;

/// <summary>
/// The run is about to ask the approver whether <paramref name="Call"/> may run (TOOL-04). It comes before the approver
/// is asked, so a host whose approver answers from the event stream sees everything that happened before it.
/// </summary>
public sealed record ApprovalAsked(ToolCall Call) : RunEvent;

/// <summary>The approver answered, or failed to (which denies the call).</summary>
public sealed record ApprovalAnswered(ToolCall Call, bool Approved) : RunEvent;

/// <summary>
/// A message was appended to the conversation (AGT-08). The run waits while the host handles the event, so the host can
/// persist the conversation after every step.
/// </summary>
public sealed record ConversationAppended(Conversation Conversation, Message Message) : RunEvent;

/// <summary>The run ended; always the last event.</summary>
public sealed record RunEnded(RunResult Result) : RunEvent;
