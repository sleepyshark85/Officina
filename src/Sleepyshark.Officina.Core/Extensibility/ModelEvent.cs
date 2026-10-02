using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Something a provider streams back during a model call, as it is generated (MDL-07).</summary>
public abstract record ModelEvent;

/// <summary>Generated text. The reply's text is the concatenation of its deltas.</summary>
public sealed record TextDelta(string Text) : ModelEvent;

/// <summary>A whole piece of the reply other than text: reasoning, a tool request, or the provider's own content (MSG-02).</summary>
public sealed record ContentReceived(Content Content) : ModelEvent;

/// <summary>A tool the provider ran itself (TOOL-13), with its result. The core audits it and counts it towards budgets.</summary>
public sealed record ProviderToolUsed(ToolRequest Request, string Result) : ModelEvent;

/// <summary>Tokens the call used so far; a call may report several times, and the amounts add up.</summary>
public sealed record UsageReported(Usage Usage) : ModelEvent;

/// <summary>Why the model stopped; the last event of a call. A call that ends without one stopped for an unknown reason.</summary>
public sealed record Stopped(StopReason Reason) : ModelEvent;

/// <summary>
/// The model gateway is trying again after a failure (REL-01), with the same model or a fallback: any reply before this
/// event is void, and the reply starts over. Usage already reported was spent and stays counted.
/// </summary>
/// <param name="Failure">What the failed attempt failed with.</param>
public sealed record ReplyRestarted(ModelFailure Failure) : ModelEvent;

/// <summary>
/// The model gateway moved to a fallback profile because the one before it stayed unavailable (MDL-04). The events that
/// follow are the fallback's.
/// </summary>
/// <param name="Name">The fallback's name in <c>models</c>.</param>
/// <param name="Profile">The fallback.</param>
/// <param name="Failure">Why the profile before it was given up on.</param>
public sealed record FallbackUsed(string Name, Configuration.ModelProfile Profile, ModelFailure Failure) : ModelEvent;
