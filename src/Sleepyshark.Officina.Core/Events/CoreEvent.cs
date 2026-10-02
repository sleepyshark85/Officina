using System.Reflection;
using System.Text.Json.Serialization;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Events;

/// <summary>Something that happened in a run, as it is published live and stored (EVT-01, EVT-02).</summary>
/// <param name="RunId">The run.</param>
/// <param name="Agent">The agent.</param>
/// <param name="Step">The step of the loop pattern; null until loop patterns (S13) add steps.</param>
/// <param name="Sequence">
/// The event's place in the stream. Every later event has a higher one, so the events of each agent are in order, and a
/// reader catches up from the last one it saw (EVT-03).
/// </param>
/// <param name="Time">When.</param>
/// <param name="Payload">What happened.</param>
public sealed record CoreEvent(string RunId, string Agent, string? Step, long Sequence, DateTimeOffset Time, EventPayload Payload);

/// <summary>What happened. Each kind has a name, which configuration uses to choose the events that are stored (EVT-05).</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TurnStarted), "turnStarted")]
[JsonDerivedType(typeof(TurnEnded), "turnEnded")]
[JsonDerivedType(typeof(TextGenerated), "textGenerated")]
[JsonDerivedType(typeof(ModelCallEnded), "modelCallEnded")]
[JsonDerivedType(typeof(CacheHitWarning), "cacheHitWarning")]
[JsonDerivedType(typeof(ToolCallStarted), "toolCallStarted")]
[JsonDerivedType(typeof(ToolCallEnded), "toolCallEnded")]
[JsonDerivedType(typeof(ToolOutput), "toolOutput")]
[JsonDerivedType(typeof(TaskStatusChanged), "taskStatusChanged")]
[JsonDerivedType(typeof(HumanAsked), "humanAsked")]
[JsonDerivedType(typeof(HumanAnswered), "humanAnswered")]
public abstract record EventPayload
{
    private static readonly Dictionary<Type, string> Names = typeof(EventPayload).GetCustomAttributes<JsonDerivedTypeAttribute>()
        .ToDictionary(kind => kind.DerivedType, kind => (string)kind.TypeDiscriminator!);

    /// <summary>The names of every kind.</summary>
    public static IReadOnlyCollection<string> Kinds => Names.Values;

    [JsonIgnore]
    public string Kind => Names[GetType()];
}

/// <summary>The agent started a turn.</summary>
public sealed record TurnStarted : EventPayload;

/// <summary>The agent's turn ended; the reason is set when it was handed off.</summary>
public sealed record TurnEnded(AgentOutcome Outcome, HandoffReason? Reason) : EventPayload;

/// <summary>Model text, as it is generated (MDL-07).</summary>
public sealed record TextGenerated(string Text) : EventPayload;

/// <summary>A model call ended, with what it used and cost.</summary>
public sealed record ModelCallEnded(StopReason Stop, Usage Usage, decimal Cost) : EventPayload;

/// <summary>A model call read less of its input from the cache than configured (COST-01).</summary>
public sealed record CacheHitWarning(CacheWarning Warning) : EventPayload;

/// <summary>A tool call reached the tool pipeline. Known secrets are removed from the arguments (INV-06).</summary>
public sealed record ToolCallStarted(string Tool, string Arguments) : EventPayload;

/// <summary>A tool call ended; the error is set when it did not succeed.</summary>
public sealed record ToolCallEnded(string Tool, ToolErrorCategory? Error) : EventPayload;

/// <summary>A line of output from a running tool, such as a sandboxed command (SBX-04). Known secrets are removed (INV-06).</summary>
public sealed record ToolOutput(string Tool, string Line) : EventPayload;

/// <summary>The agent waits for a human: an approval, a question or a sign-off (UX-01).</summary>
/// <param name="Request">What it waits for.</param>
/// <param name="Summary">What is asked.</param>
/// <param name="Tool">For an approval, the tool.</param>
public sealed record HumanAsked(HumanRequestKind Request, string Summary, string? Tool = null) : EventPayload;

/// <summary>A task on the run's board has a new status (TASK-02).</summary>
public sealed record TaskStatusChanged(string Task, TaskState Status) : EventPayload;

/// <summary>The wait ended: approved (or the question answered), denied, or with no answer by the deadline.</summary>
public sealed record HumanAnswered(HumanRequestKind Request, bool Approved, bool TimedOut, string? Tool = null) : EventPayload;
