namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What an audit entry records about a tool call.</summary>
public enum AuditOutcome
{
    /// <summary>A rule, gate or human denied the call.</summary>
    Denied,

    /// <summary>A human was asked to approve the call.</summary>
    Asked,

    /// <summary>The call was routed to another agent or a human.</summary>
    Routed,

    /// <summary>The call was allowed and is about to run; written before it runs (TOOL-10).</summary>
    Intent,

    Completed,

    Failed,
}
