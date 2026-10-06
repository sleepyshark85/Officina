namespace Sleepyshark.Officina;

/// <summary>Something that happened during a run, streamed to the host as it happens.</summary>
public abstract record RunEvent;

/// <summary>A piece of the model's reply text.</summary>
public sealed record TextStreamed(string Text) : RunEvent;

/// <summary>The model call was retried after its reply had started: discard the text streamed so far.</summary>
public sealed record ReplyRestarted : RunEvent;

/// <summary>Tokens a model call reported, and their cost in US dollars (zero without a price).</summary>
public sealed record UsageReported(Usage Usage, decimal Cost) : RunEvent;

/// <summary>The run began handling a tool call: its approval, if needed, then the tool.</summary>
public sealed record ToolCallStarted(ToolCall Call) : RunEvent;

/// <summary>A tool call got its result, as the model will see it; every call gets one.</summary>
public sealed record ToolCallFinished(ToolCall Call, ToolResult Result) : RunEvent;

/// <summary>
/// The run is about to ask the approver whether <paramref name="Call"/> may run. It follows everything before it in the
/// stream, but may reach the host before or after the approver is asked.
/// </summary>
public sealed record ApprovalAsked(ToolCall Call) : RunEvent;

/// <summary>The approver answered, or failed to, which denies the call.</summary>
public sealed record ApprovalAnswered(ToolCall Call, bool Approved) : RunEvent;

/// <summary>A message was appended. The run waits while the host handles it, so the host can save after every step.</summary>
public sealed record ConversationAppended(Conversation Conversation, Message Message) : RunEvent;

/// <summary>The run ended; always the last event.</summary>
public sealed record RunEnded(RunResult Result) : RunEvent;
