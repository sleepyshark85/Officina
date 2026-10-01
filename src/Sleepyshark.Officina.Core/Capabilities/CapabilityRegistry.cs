using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Capabilities;

/// <summary>The capabilities a host makes available. Configuration can switch on only registered capabilities.</summary>
public sealed partial class CapabilityRegistry
{
    private readonly SortedDictionary<string, ICapability> capabilities = new(StringComparer.Ordinal);

    public CapabilityRegistry(IEnumerable<ICapability> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        foreach (var capability in capabilities)
        {
            if (!ValidName().IsMatch(capability.Name))
            {
                throw new ArgumentException($"Capability name \"{capability.Name}\" must be a camelCase identifier.", nameof(capabilities));
            }

            if (!typeof(CapabilitySettings).IsAssignableFrom(capability.SettingsType))
            {
                throw new ArgumentException($"Capability {capability.Name} has settings type {capability.SettingsType.Name}, which does not derive from {nameof(CapabilitySettings)}.", nameof(capabilities));
            }

            if (!this.capabilities.TryAdd(capability.Name, capability))
            {
                throw new ArgumentException($"Capability {capability.Name} is registered twice.", nameof(capabilities));
            }
        }
    }

    public static CapabilityRegistry Empty { get; } = new([]);

    /// <summary>Every registered capability, sorted by name.</summary>
    public IReadOnlyCollection<ICapability> All => capabilities.Values;

    public ICapability? Find(string name) => capabilities.GetValueOrDefault(name);

    /// <summary>The settings a capability has when configuration does not mention it: off, with its defaults.</summary>
    public static CapabilitySettings DefaultSettings(ICapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        return (CapabilitySettings)Activator.CreateInstance(capability.SettingsType)!;
    }

    /// <summary>Collects what the capabilities that are on contribute. Capabilities that are off are not asked (CAP-02).</summary>
    public CapabilityContributions Compose(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var contributions = new CapabilityContributions();
        foreach (var (name, settings) in options.Capabilities)
        {
            if (settings.Enabled && Find(name) is { } capability)
            {
                capability.Contribute(settings, contributions);
            }
        }

        return contributions;
    }

    [GeneratedRegex("^[a-z][A-Za-z0-9]*$")]
    private static partial Regex ValidName();
}
