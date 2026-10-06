using System.Collections.Immutable;

namespace Sleepyshark.Officina;

/// <summary>What one model call returned.</summary>
internal sealed class ModelReply
{
    public ImmutableArray<ContentBlock>.Builder Blocks { get; } = ImmutableArray.CreateBuilder<ContentBlock>();

    public Usage Usage { get; set; }

    public ModelStopped? Stop { get; set; }

    public string? Error { get; set; }

    /// <summary>What the provider did to shorten the conversation for this call, for the audit trail.</summary>
    public List<(AuditKind Kind, string Detail)> ContextEdits { get; } = [];

    public int Retries { get; set; }

    /// <summary>How long the first text took to arrive, if any did.</summary>
    public TimeSpan? FirstText { get; set; }
}
