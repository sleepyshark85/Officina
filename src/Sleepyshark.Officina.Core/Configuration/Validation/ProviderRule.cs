using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Configuration.Validation;

/// <summary>Model profiles use only what their provider declares: efforts, settings, tool choices and output length (phase 6, MDL-02).</summary>
internal sealed class ProviderRule : IConfigurationRule
{
    public ValidationPhase Phase => ValidationPhase.Provider;

    public IEnumerable<ConfigurationError> Check(ValidationContext context)
    {
        var profiles = context.Options.Models.Select(entry => (Path: SettingPath.Child("models", entry.Key), Profile: entry.Value))
            .Concat(context.Options.Agents
                .Where(entry => entry.Value.Model?.Profile is not null)
                .Select(entry => (Path: SettingPath.Child(SettingPath.Child("agents", entry.Key), "model"), Profile: entry.Value.Model.Profile!)));

        foreach (var (path, profile) in profiles)
        {
            if (context.DescribeProvider(profile.Provider) is not { } declared)
            {
                continue;
            }

            foreach (var error in CheckProfile(path, profile, declared))
            {
                yield return error;
            }
        }
    }

    private IEnumerable<ConfigurationError> CheckProfile(string path, ModelProfile profile, ProviderCapabilities declared)
    {
        var provider = profile.Provider;
        if (profile.Effort is { } effort && declared.Efforts is { } efforts && !efforts.Contains(effort))
        {
            yield return new ConfigurationError(Phase, SettingPath.Child(path, "effort"), $"provider {provider} does not support effort \"{effort}\".",
                Choose(efforts, effort));
        }

        if (!declared.ToolChoices.Contains(profile.ToolChoice))
        {
            yield return new ConfigurationError(Phase, SettingPath.Child(path, "toolChoice"), $"provider {provider} does not support tool choice \"{profile.ToolChoice}\".",
                Choose(declared.ToolChoices, profile.ToolChoice));
        }

        if (profile.MaxOutputTokens > declared.MaxOutputTokens)
        {
            yield return new ConfigurationError(Phase, SettingPath.Child(path, "maxOutputTokens"),
                $"is {profile.MaxOutputTokens}, but provider {provider} allows at most {declared.MaxOutputTokens}.",
                $"Use {declared.MaxOutputTokens} or less.");
        }

        if (declared.Settings is { } settings)
        {
            foreach (var name in profile.Settings.Keys.Where(name => !settings.Contains(name)))
            {
                yield return new ConfigurationError(Phase, SettingPath.Child(SettingPath.Child(path, "settings"), name),
                    $"provider {provider} does not declare the setting \"{name}\".",
                    (settings.Count == 0 ? "It declares no settings; remove it." : $"It declares: {string.Join(", ", settings)}.") + Suggestions.DidYouMean(name, settings));
            }
        }
    }

    private static string Choose(IReadOnlyCollection<string> allowed, string given) =>
        $"Use one of: {string.Join(", ", allowed)}.{Suggestions.DidYouMean(given, allowed)}";
}
