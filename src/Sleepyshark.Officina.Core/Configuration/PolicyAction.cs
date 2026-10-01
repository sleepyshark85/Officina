namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>What a permission rule or a gate decides about a tool call (TOOL-05).</summary>
public enum PolicyAction
{
    /// <summary>The call goes on to the next step.</summary>
    Allow,

    /// <summary>The call does not run, and the model is told why.</summary>
    Deny,

    /// <summary>A human approves, changes or denies the call.</summary>
    Ask,

    /// <summary>The call does not run; the turn is handed to another agent or a human.</summary>
    Route,
}
