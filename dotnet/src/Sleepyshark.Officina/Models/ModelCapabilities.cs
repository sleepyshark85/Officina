namespace Sleepyshark.Officina;

/// <summary>What a model's provider supports beyond the basic contract.</summary>
[Flags]
public enum ModelCapabilities
{
    None = 0,

    /// <summary>Server-side compaction of the conversation into a summary block.</summary>
    Compaction = 1,

    /// <summary>Server-side clearing of old tool results.</summary>
    ContextEditing = 2,
}
