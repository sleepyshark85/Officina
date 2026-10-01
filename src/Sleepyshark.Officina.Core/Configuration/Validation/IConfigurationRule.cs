using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>
/// A validation rule over the bound Options. The core has its rules; each capability and later slice adds
/// its own, for the phase it belongs to (configuration reference §14).
/// </summary>
public interface IConfigurationRule
{
    ValidationPhase Phase { get; }

    IEnumerable<ConfigurationError> Check(ValidationContext context);
}

/// <summary>What a rule can see.</summary>
public sealed class ValidationContext
{
    public ValidationContext(OfficinaOptions options, SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        Options = options;
        Model = model;
    }

    public OfficinaOptions Options { get; }

    public SettingsModel Model { get; }

    /// <summary>What a provider supports, by its name in <c>providers</c>, or null when the host does not say.</summary>
    public Func<string, ProviderCapabilities?> DescribeProvider { get; init; } = _ => null;

    /// <summary>The provider types the host can create, or null when the host does not check them.</summary>
    public IReadOnlySet<string>? ProviderTypes { get; init; }
}
