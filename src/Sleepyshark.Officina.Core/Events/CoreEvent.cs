using System.Reflection;
using System.Text.Json.Serialization;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Events;

/// <summary>Something that happened in a run, as it is published live and stored (EVT-01, EVT-02).</summary>
/// <param name="RunId">The run.</param>
/// <param name="Agent">The agent.</param>
/// <param name="Step">The step of the agent's pattern, as a path such as <c>fix/review</c>; null for the agent's own turn.</param>
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
[JsonDerivedType(typeof(ModelFallback), "modelFallback")]
[JsonDerivedType(typeof(CacheHitWarning), "cacheHitWarning")]
[JsonDerivedType(typeof(BudgetWarning), "budgetWarning")]
[JsonDerivedType(typeof(CheckRan), "checkRan")]
[JsonDerivedType(typeof(ToolCallStarted), "toolCallStarted")]
[JsonDerivedType(typeof(ToolCallEnded), "toolCallEnded")]
[JsonDerivedType(typeof(ToolOutput), "toolOutput")]
[JsonDerivedType(typeof(TaskStatusChanged), "taskStatusChanged")]
[JsonDerivedType(typeof(HumanAsked), "humanAsked")]
[JsonDerivedType(typeof(HumanAnswered), "humanAnswered")]
[JsonDerivedType(typeof(StepEnded), "stepEnded")]
[JsonDerivedType(typeof(CheckpointTaken), "checkpointTaken")]
[JsonDerivedType(typeof(RunResumed), "runResumed")]
[JsonDerivedType(typeof(RunRolledBack), "runRolledBack")]
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

/// <summary>A model call ended, with what it used and cost, and the model and task they are counted under (RUN-10).</summary>
/// <param name="Stop">Why the call stopped.</param>
/// <param name="Usage">What it used.</param>
/// <param name="Cost">What it cost, in USD.</param>
/// <param name="Model">The model that served it, which is a fallback's when the gateway used one.</param>
/// <param name="Task">The task the work is for, if any.</param>
public sealed record ModelCallEnded(StopReason Stop, Usage Usage, decimal Cost, string? Model = null, string? Task = null) : EventPayload;

/// <summary>The model gateway served a call with a fallback profile because the one before it stayed unavailable (MDL-04).</summary>
/// <param name="Profile">The fallback's name in <c>models</c>.</param>
/// <param name="Provider">Its provider.</param>
/// <param name="Model">Its model.</param>
/// <param name="Failure">Why the profile before it was given up on.</param>
public sealed record ModelFallback(string Profile, string Provider, string Model, ModelFailure Failure) : EventPayload;

/// <summary>A model call read less of its input from the cache than configured (COST-01).</summary>
public sealed record CacheHitWarning(CacheWarning Warning) : EventPayload;

/// <summary>A budget is nearly used up: it has used the share of one of its limits that the warning is set at (EVT-01, RUN-05).</summary>
/// <param name="Level">Whose budget: the run's, the agent's, the pattern's, the turn's or the task's.</param>
/// <param name="Limit">Which limit: iteration, tool-call, token, cost or time.</param>
/// <param name="Used">The share of the limit used, such as 0.8.</param>
public sealed record BudgetWarning(string Level, string Limit, double Used) : EventPayload;

/// <summary>A check ran, on an agent's output or a task's work, and passed or failed (RUN-11).</summary>
public sealed record CheckRan(string Check, bool Passed, string? Task = null) : EventPayload;

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

/// <summary>A step of the agent's pattern ended (PAT-08); the reason is set when it was handed off or cancelled.</summary>
public sealed record StepEnded(StepOutcome Outcome, HandoffReason? Reason) : EventPayload;

/// <summary>The run took a checkpoint (RUN-03).</summary>
public sealed record CheckpointTaken(int Number, CheckpointPoint Point) : EventPayload;

/// <summary>
/// The run started again after a crash or a restart, from a checkpoint (RUN-04). The calls whose outcome is unknown are flagged;
/// an irreversible one is not run again (RUN-07).
/// </summary>
public sealed record RunResumed(int Checkpoint, IReadOnlyList<ToolEffect> Interrupted) : EventPayload;

/// <summary>
/// The run was rolled back to a checkpoint (RUN-08). The effects outside the core's state made since are listed; they are not
/// undone, and neither are the project memory changes made since, which are counted.
/// </summary>
public sealed record RunRolledBack(int Checkpoint, IReadOnlyList<ToolEffect> NotUndone, int MemoryChanges) : EventPayload;
