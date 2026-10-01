namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What a provider supports. Capabilities are added as the features that need them arrive (MDL-06).</summary>
public sealed record ProviderCapabilities
{
    public static ProviderCapabilities None { get; } = new();

    /// <summary>The most cache boundaries one request may carry (CTX-11); 0 when the provider has no cache.</summary>
    public int CacheBoundaries { get; init; }

    /// <summary>
    /// Whether the provider takes turn-scoped system messages, shown for one model call and then cleared. Without them
    /// the volatile context is kept in the history (CTX-10).
    /// </summary>
    public bool TurnScopedMessages { get; init; }
}
