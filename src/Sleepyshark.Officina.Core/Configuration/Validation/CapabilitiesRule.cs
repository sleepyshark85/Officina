using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>Capabilities exist, their dependencies are on, and agents use only capabilities that are on (phase 5, CAP-03).</summary>
internal sealed class CapabilitiesRule : IConfigurationRule
{
    public ValidationPhase Phase => ValidationPhase.Capabilities;

    public IEnumerable<ConfigurationError> Check(ValidationContext context)
    {
        var options = context.Options;
        var registry = context.Model.Capabilities;
        var available = registry.All.Select(capability => capability.Name).ToArray();
        foreach (var (name, settings) in options.Capabilities)
        {
            var path = SettingPath.Child("capabilities", name);
            var capability = registry.Find(name);
            if (capability is null)
            {
                yield return new ConfigurationError(Phase, path, $"capability \"{name}\" is not available in this application.",
                    Available(available) + Suggestions.DidYouMean(name, available));
                continue;
            }

            if (!capability.SettingsType.IsInstanceOfType(settings))
            {
                yield return new ConfigurationError(Phase, path, $"has settings of type {settings.GetType().Name}, but {name} takes {capability.SettingsType.Name}.",
                    $"Use {capability.SettingsType.Name}.");
                continue;
            }

            if (!settings.Enabled)
            {
                continue;
            }

            foreach (var required in capability.Requires(settings).Where(required => !options.IsEnabled(required)))
            {
                yield return new ConfigurationError(Phase, path, $"{name} needs {required}, which is off.",
                    $"Turn on capabilities.{required}, or turn {name} off (CAP-03).");
            }
        }

        foreach (var (agentName, agent) in options.Agents)
        {
            if (agent.Capabilities is not { } used)
            {
                continue;
            }

            for (var index = 0; index < used.Count; index++)
            {
                if (!options.IsEnabled(used[index]))
                {
                    var path = SettingPath.Item(SettingPath.Child(SettingPath.Child("agents", agentName), "capabilities"), index);
                    yield return new ConfigurationError(Phase, path, $"the agent uses {used[index]}, which the application has not enabled.",
                        (registry.Find(used[index]) is null ? Available(available) : $"Turn on capabilities.{used[index]}, or remove it here (CAP-03).") + Suggestions.DidYouMean(used[index], available));
                }
            }
        }
    }

    private static string Available(string[] names) =>
        names.Length == 0 ? "This application has no capabilities registered." : "Available capabilities: " + string.Join(", ", names) + ".";
}
