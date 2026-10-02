using System.Text.Json;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>One call of a tool, as the tool receives it.</summary>
/// <param name="Arguments">The arguments, already validated against the tool's input schema.</param>
/// <param name="Caller">Who the tool acts for (INV-02). It comes from the host, never from the model or the arguments (INV-03).</param>
/// <param name="IdempotencyKey">Stable for the same run, tool and arguments, so downstream systems can reject duplicates (TOOL-10).</param>
/// <param name="Secrets">
/// Where the tool reads credentials when it runs (SEC-05). Every value read here is kept out of the model's input, the
/// audit log and errors (INV-06).
/// </param>
/// <param name="Record">The run record, to read; only the core's record tools propose changes to it (REC-02).</param>
/// <param name="Board">The task board, to read, with the task the agent works on; null when the task board is off.</param>
/// <param name="Output">Publishes a line of the tool's output as it is produced, as an event, with known secrets removed.</param>
public sealed record ToolCall(
    JsonElement Arguments, Caller Caller, string IdempotencyKey, ISecretSource Secrets, RunRecord Record, TaskBoard? Board,
    Func<string, CancellationToken, ValueTask> Output)
{
    /// <summary>The agent that makes the call, set by the host's pipeline, so a tool shared by agents can act for each (such as in its own working copy).</summary>
    public string Agent { get; init; } = "";

    /// <summary>
    /// The name of the working copy the call works in, which a shared tool opens by it (<see cref="WorkingCopies"/>): its task's,
    /// which the agents working on and reviewing the task share, or else the agent's own.
    /// </summary>
    public string WorkingCopy { get; init; } = "";

    /// <summary>Project memory as the agent acts on it; null when project memory is off.</summary>
    public ProjectMemory? Memory { get; init; }

    /// <summary>Sends another agent of the caller's team a message; it returns why it cannot, or null. Null when the agent is in no team.</summary>
    internal Func<string, string, CancellationToken, ValueTask<string?>>? Send { get; init; }
}
