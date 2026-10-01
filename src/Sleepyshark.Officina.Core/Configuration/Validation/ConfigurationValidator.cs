using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>
/// Validates bound Options in full and reports every error, phase by phase (CFG-06). Files add the
/// parse, shape and merge errors that only they can have; the programmatic form gets the rest from here.
/// </summary>
public sealed class ConfigurationValidator
{
    private static readonly IConfigurationRule[] CoreRules =
        [new SettingValuesRule(), new ReferencesRule(), new PlaceholdersRule(), new CapabilitiesRule(), new ProviderRule()];

    private readonly IReadOnlyList<IConfigurationRule> extraRules;

    /// <param name="model">The settings model, with the host's capabilities.</param>
    /// <param name="extraRules">More rules from the host, for example from extensions.</param>
    public ConfigurationValidator(SettingsModel model, IEnumerable<IConfigurationRule>? extraRules = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        this.extraRules = [.. extraRules ?? []];
    }

    public SettingsModel Model { get; }

    public IReadOnlyList<ConfigurationError> Validate(OfficinaOptions options) => Validate(new ValidationContext(options, Model));

    public IReadOnlyList<ConfigurationError> Validate(ValidationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rules = CoreRules.Concat(CapabilityRules(context.Options, Model.Capabilities)).Concat(extraRules);
        return [.. rules
            .SelectMany(rule => rule.Check(context))
            .Distinct()
            .Select((error, order) => (error, order))
            .OrderBy(entry => entry.error.Phase)
            .ThenBy(entry => entry.order)
            .Select(entry => entry.error)];
    }

    /// <summary>The rules of the capabilities that are on. Capabilities that are off add none (CAP-02).</summary>
    private static IEnumerable<IConfigurationRule> CapabilityRules(OfficinaOptions options, CapabilityRegistry registry) =>
        registry.All.Where(capability => options.IsEnabled(capability.Name)).SelectMany(capability => capability.Rules);
}
