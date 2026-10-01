using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>What the coding team CLI is made of. Capabilities, presets and providers are added here as their slices ship.</summary>
public static class SofApplication
{
    public static CapabilityRegistry Capabilities { get; } = CapabilityRegistry.Empty;

    public static PresetCatalog Presets { get; } = PresetCatalog.Empty;

    /// <summary>The provider types the CLI can create. The Claude provider declares what it supports when it ships (S11).</summary>
    public static IReadOnlyDictionary<string, ProviderCapabilities> ProviderTypes { get; } =
        new Dictionary<string, ProviderCapabilities> { ["claude"] = ProviderCapabilities.None };

    public static ConfigurationLoader CreateLoader() => new(Capabilities, Presets, ProviderTypes);
}
