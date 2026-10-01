using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Placeholders;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>
/// Placeholders in instructions can be filled from the project and the agent definition only (CFG-14).
/// Unknown names are phase 4, volatile namespaces phase 9 and secrets phase 10.
/// </summary>
internal sealed class PlaceholdersRule : IConfigurationRule
{
    public ValidationPhase Phase => ValidationPhase.References;

    public IEnumerable<ConfigurationError> Check(ValidationContext context)
    {
        foreach (var (name, agent) in context.Options.Agents)
        {
            if (agent.Instructions is null)
            {
                continue;
            }

            var values = new StablePrefixValues(context.Options.Project, name, agent);
            var path = SettingPath.Child(SettingPath.Child("agents", name), "instructions");
            foreach (var error in PlaceholderRules.Check(agent.Instructions, path, PlaceholderScope.StablePrefix, values))
            {
                yield return error;
            }
        }
    }
}
