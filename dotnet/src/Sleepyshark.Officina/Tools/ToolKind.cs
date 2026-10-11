namespace Sleepyshark.Officina;

/// <summary>Whether a tool only reads, or changes something.</summary>
public enum ToolKind
{
    /// <summary>Reads only: a reply's read calls run concurrently.</summary>
    Read,

    /// <summary>Changes something: write calls run one at a time, in order, each audited before it runs.</summary>
    Write,
}
