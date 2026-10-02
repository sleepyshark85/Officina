using System.Text.Json;
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
}
