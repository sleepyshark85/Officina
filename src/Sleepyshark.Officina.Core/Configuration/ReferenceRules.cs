namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Every name a setting refers to exists: providers, model profiles, fallbacks, and secret names (phase 4).</summary>
internal static class ReferenceRules
{
    public static IEnumerable<ConfigurationError> Check(OfficinaOptions options)
    {
        foreach (var (name, provider) in options.Providers)
        {
            if (provider.ApiKey is { } secret && !SecretReference.IsValidName(secret.Name))
            {
                yield return new(ValidationPhase.References, $"providers.{name}.apiKey.secret", $"\"{secret.Name}\" is not a secret name.",
                    "A secret name has letters, digits and underscores, such as ANTHROPIC_API_KEY.");
            }
        }

        foreach (var (name, profile) in options.Models)
        {
            if (!options.Providers.ContainsKey(profile.Provider))
            {
                yield return Missing($"models.{name}.provider", "provider", profile.Provider, "providers", options.Providers.Keys);
            }

            foreach (var (fallback, index) in profile.Fallbacks.Select((fallback, index) => (fallback, index)))
            {
                if (fallback == name)
                {
                    yield return new(ValidationPhase.References, $"models.{name}.fallbacks[{index}]", "a profile cannot be its own fallback.", "Name a different profile.");
                }
                else if (!options.Models.ContainsKey(fallback))
                {
                    yield return Missing($"models.{name}.fallbacks[{index}]", "model profile", fallback, "models", options.Models.Keys);
                }
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
        new(ValidationPhase.References, path, $"{kind} \"{name}\" does not exist.", $"Add it to {section}, or use one of: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
}
