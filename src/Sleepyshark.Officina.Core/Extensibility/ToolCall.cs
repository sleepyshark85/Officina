using System.Text.Json;
using Sleepyshark.Officina.Core.Records;

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
public sealed record ToolCall(JsonElement Arguments, Caller Caller, string IdempotencyKey, ISecretSource Secrets, RunRecord Record);
