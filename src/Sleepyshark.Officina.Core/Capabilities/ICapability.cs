using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Core.Capabilities;

/// <summary>
/// An optional capability, such as the workspace or the task board (CAP-01). A capability declares its
/// settings, its dependencies and its validation rules; it contributes tools and storage only while it
/// is on, so a capability that is off adds nothing (CAP-02).
/// </summary>
public interface ICapability
{
    /// <summary>The name under <c>capabilities</c>, such as <c>taskBoard</c>.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>The capability's settings record, derived from <see cref="CapabilitySettings"/>.</summary>
    Type SettingsType { get; }

    /// <summary>The capabilities this one needs with these settings (CAP-03).</summary>
    IEnumerable<string> Requires(CapabilitySettings settings);

    /// <summary>Validation rules that run only while the capability is on.</summary>
    IEnumerable<IConfigurationRule> Rules { get; }

    /// <summary>Adds the capability's tools and storage. Called only while the capability is on.</summary>
    void Contribute(CapabilitySettings settings, CapabilityContributions contributions);
}

/// <summary>A convenient base for capabilities, typed by their settings record.</summary>
public abstract class Capability<TSettings> : ICapability
    where TSettings : CapabilitySettings, new()
{
    public abstract string Name { get; }

    public abstract string Description { get; }

    public Type SettingsType => typeof(TSettings);

    public virtual IEnumerable<IConfigurationRule> Rules => [];

    public IEnumerable<string> Requires(CapabilitySettings settings) => Requires((TSettings)settings);

    public void Contribute(CapabilitySettings settings, CapabilityContributions contributions) => Contribute((TSettings)settings, contributions);

    protected virtual IEnumerable<string> Requires(TSettings settings) => [];

    protected virtual void Contribute(TSettings settings, CapabilityContributions contributions)
    {
    }
}
