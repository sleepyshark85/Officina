namespace Sleepyshark.Officina;

/// <summary>
/// What one run gets beside its message, each optional (ARCHITECTURE §5). None of it is part of the prefix, so each run
/// of a conversation may have its own.
/// </summary>
public sealed record RunOptions
{
    /// <summary>The run context, appended after the user message as an operator message (CTX-02).</summary>
    public string? Context
    {
        get;
        init
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value);
            }

            field = value;
        }
    }

    /// <summary>
    /// Whose memory the run sees (MEM-03), one of the scopes <see cref="MemoryPath"/> accepts; an agent with the memory
    /// tool needs one. Tool handlers get it in their <see cref="ToolContext"/>.
    /// </summary>
    public string? MemoryScope
    {
        get;
        init
        {
            if (value is not null)
            {
                MemoryPath.Check(value);
            }

            field = value;
        }
    }

    /// <summary>Limits on the run (BUD-01); none by default. A host may give each run what is left of a session's budget.</summary>
    public Budget? Budget { get; init; }
}
