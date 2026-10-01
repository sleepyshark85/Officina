using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>Every name a setting refers to exists: model profiles, providers, provider types and secret names (phase 4).</summary>
internal sealed class ReferencesRule : IConfigurationRule
{
    public ValidationPhase Phase => ValidationPhase.References;

    public IEnumerable<ConfigurationError> Check(ValidationContext context)
    {
        var options = context.Options;
        foreach (var (name, provider) in options.Providers)
        {
            var path = SettingPath.Child("providers", name);
            if (context.ProviderTypes is { } types && !types.Contains(provider.Type))
            {
                yield return Error(SettingPath.Child(path, "type"), $"provider type \"{provider.Type}\" is not available.",
                    $"Use one of: {string.Join(", ", types.Order(StringComparer.Ordinal))}.{Suggestions.DidYouMean(provider.Type, types)}");
            }

            if (provider.ApiKey is { } secret && !SecretReference.IsValidName(secret.Name))
            {
                yield return Error(SettingPath.Child(path, "apiKey"), $"\"{secret.Name}\" is not a secret name.",
                    "A secret name has letters, digits and underscores, such as ANTHROPIC_API_KEY.");
            }
        }

        foreach (var (name, profile) in options.Models)
        {
            foreach (var error in CheckProfile(options, profile, SettingPath.Child("models", name), name))
            {
                yield return error;
            }
        }

        foreach (var (name, agent) in options.Agents)
        {
            var path = SettingPath.Child(SettingPath.Child("agents", name), "model");
            if (agent.Model is null)
            {
                continue;
            }

            if (agent.Model.Name is { } profileName && !options.Models.ContainsKey(profileName))
            {
                yield return Error(path, $"model profile \"{profileName}\" does not exist.",
                    $"Add it to models, or use one of: {string.Join(", ", options.Models.Keys)}.{Suggestions.DidYouMean(profileName, options.Models.Keys)}");
            }
            else if (agent.Model.Profile is { } inline)
            {
                foreach (var error in CheckProfile(options, inline, path, null))
                {
                    yield return error;
                }
            }
        }
    }

    private static IEnumerable<ConfigurationError> CheckProfile(OfficinaOptions options, ModelProfile profile, string path, string? ownName)
    {
        if (!options.Providers.ContainsKey(profile.Provider))
        {
            yield return Error(SettingPath.Child(path, "provider"), $"provider \"{profile.Provider}\" does not exist.",
                $"Add it to providers, or use one of: {string.Join(", ", options.Providers.Keys)}.{Suggestions.DidYouMean(profile.Provider, options.Providers.Keys)}");
        }

        for (var index = 0; index < profile.Fallbacks.Count; index++)
        {
            var fallback = profile.Fallbacks[index];
            var fallbackPath = SettingPath.Item(SettingPath.Child(path, "fallbacks"), index);
            if (fallback == ownName)
            {
                yield return Error(fallbackPath, "a profile cannot be its own fallback.", "Name a different profile.");
            }
            else if (!options.Models.ContainsKey(fallback))
            {
                yield return Error(fallbackPath, $"model profile \"{fallback}\" does not exist.",
                    $"Add it to models.{Suggestions.DidYouMean(fallback, options.Models.Keys)}");
            }
        }
    }

    private static ConfigurationError Error(string path, string problem, string fix) => new(ValidationPhase.References, path, problem, fix);
}
