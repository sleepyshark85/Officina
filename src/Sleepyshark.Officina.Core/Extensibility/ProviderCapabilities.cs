namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>What a provider supports. Capabilities are added as the features that need them arrive (MDL-06).</summary>
public sealed record ProviderCapabilities
{
    public static ProviderCapabilities None { get; } = new();
}
