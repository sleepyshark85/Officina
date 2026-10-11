namespace Sleepyshark.Officina;

/// <summary>A tool source's connection state, as the audit trail records it.</summary>
public enum ToolSourceState
{
    Connected,

    /// <summary>An attempt to connect failed, or the source could not report its changes.</summary>
    Failed,

    /// <summary>A connection was lost.</summary>
    Disconnected,
}
