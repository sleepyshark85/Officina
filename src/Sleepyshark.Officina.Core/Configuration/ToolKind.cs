namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Whether a tool only reads, or changes something. Every write tool is gated and audited (INV-04, INV-05).</summary>
public enum ToolKind
{
    Read,
    Write,
}
