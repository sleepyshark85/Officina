namespace Sleepyshark.Officina;

/// <summary>How a tool call ended, as its span and metrics name it.</summary>
internal enum ToolOutcome
{
    Ok,
    Error,

    /// <summary>A write that did not run, as its attempt could not be audited.</summary>
    Blocked,
}
