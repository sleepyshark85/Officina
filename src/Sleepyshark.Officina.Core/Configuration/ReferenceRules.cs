namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The names settings refer to exist: an agent's model profile, and a profile's provider (phase 4).</summary>
internal static class ReferenceRules
{
    public static IEnumerable<ConfigurationError> Check(OfficinaOptions options)
    {
        foreach (var (name, profile) in options.Models)
        {
            if (!options.Providers.ContainsKey(profile.Provider))
            {
                yield return Missing($"models.{name}.provider", "provider", profile.Provider, "providers", options.Providers.Keys);
            }
        }

        foreach (var (name, agent) in options.Agents)
        {
            if (!options.Models.ContainsKey(agent.Model))
            {
                yield return Missing($"agents.{name}.model", "model profile", agent.Model, "models", options.Models.Keys);
            }
        }
    }

    private static ConfigurationError Missing(string path, string kind, string name, string section, IEnumerable<string> known) =>
        new(ValidationPhase.References, path, $"{kind} \"{name}\" does not exist.",
            $"Add it to {section}, or use one of: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
}
