namespace Sleepyshark.Officina;

/// <summary>What one run gets besides its message, each optional. None of it is part of the prefix.</summary>
public sealed record RunOptions
{
    /// <summary>The run context, appended after the user message as an operator message.</summary>
    public string? Context
    {
        get;
        init
        {
            if (value is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Context));
            }

            field = value;
        }
    }

    /// <summary>
    /// Whose memory the run sees, a scope <see cref="MemoryPath"/> accepts; an agent with the memory tool needs one. Tool
    /// handlers get it in their <see cref="ToolContext"/>.
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

    /// <summary>Limits on the run; none by default. A host may give each run what is left of a session's budget.</summary>
    public Budget? Budget { get; init; }
}
