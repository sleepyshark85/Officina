namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>One step of a write-tool attempt (TOOL-11). Known secrets are removed from every text in it (INV-06).</summary>
/// <param name="RunId">The run.</param>
/// <param name="Agent">The agent that made the call.</param>
/// <param name="Caller">The caller's id; null for an anonymous caller.</param>
/// <param name="Tool">The tool, by its configured name.</param>
/// <param name="Arguments">The arguments, as JSON.</param>
/// <param name="DecidedBy">The rule, gate or person that decided, such as <c>gates.issue-dedupe</c>; null when nothing stopped the call.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Time">When.</param>
/// <param name="IdempotencyKey">Identifies the run, tool and arguments (TOOL-10).</param>
/// <param name="Detail">Internal details, such as a failure's exception message, that the model never sees (TOOL-08).</param>
public sealed record AuditEntry(
    string RunId,
    string Agent,
    string? Caller,
    string Tool,
    string Arguments,
    string? DecidedBy,
    AuditOutcome Outcome,
    DateTimeOffset Time,
    string IdempotencyKey,
    string? Detail = null);
