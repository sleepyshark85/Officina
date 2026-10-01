using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>One effective setting: its path, its value as JSON, and where it came from (CFG-04).</summary>
public sealed record EffectiveSetting(string Path, string Value, ConfigOrigin Origin);

/// <summary>A loaded configuration: the bound Options, every validation error, and the origin of every value.</summary>
public sealed class LoadedConfiguration
{
    private readonly OriginIndex origins;

    internal LoadedConfiguration(OfficinaOptions options, IReadOnlyList<ConfigurationError> errors, SettingsModel model, OriginIndex origins, IReadOnlyList<string> layers)
    {
        Options = options;
        Errors = errors;
        Model = model;
        this.origins = origins;
        Layers = layers;
    }

    /// <summary>The effective configuration. Use it only when <see cref="IsValid"/>.</summary>
    public OfficinaOptions Options { get; }

    public IReadOnlyList<ConfigurationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public SettingsModel Model { get; }

    /// <summary>The layers that were read, lowest first, below the code defaults.</summary>
    public IReadOnlyList<string> Layers { get; }

    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public OfficinaOptions ValidOptions() => IsValid ? Options : throw new ConfigurationException(Errors);

    public ConfigOrigin OriginOf(string path) => origins.OriginOf(path);

    /// <summary>
    /// Every effective setting with its origin. For an agent, the settings that apply to it: the project,
    /// its definition, its model profiles and providers, the run defaults and the capabilities that are on.
    /// </summary>
    /// <exception cref="ArgumentException">There is no such agent.</exception>
    public IReadOnlyList<EffectiveSetting> Settings(string? agent = null)
    {
        var settings = new List<EffectiveSetting>();
        Flatten(OptionsWriter.Write(Options, Model), "", settings);
        if (agent is null)
        {
            return settings;
        }

        var scope = ScopeOf(agent);
        return [.. settings.Where(setting => scope.Any(prefix => SettingPath.IsWithin(setting.Path, prefix)))];
    }

    private List<string> ScopeOf(string agentName)
    {
        if (!Options.Agents.TryGetValue(agentName, out var agent))
        {
            throw new ArgumentException($"There is no agent \"{agentName}\". Agents: {(Options.Agents.Count == 0 ? "none" : string.Join(", ", Options.Agents.Keys))}.", nameof(agentName));
        }

        var scope = new List<string> { "project", SettingPath.Child("agents", agentName), "run" };
        var profiles = new List<ModelProfile>();
        var inline = agent.Model.Profile;
        if (inline is not null)
        {
            profiles.Add(inline);
        }

        // The named profile, or the inline one's fallbacks, and their fallbacks in turn.
        var pending = new Queue<string>(agent.Model.Name is { } name ? [name] : inline!.Fallbacks);
        while (pending.TryDequeue(out var profileName))
        {
            var path = SettingPath.Child("models", profileName);
            if (!scope.Contains(path) && Options.Models.TryGetValue(profileName, out var profile))
            {
                scope.Add(path);
                profiles.Add(profile);
                foreach (var fallback in profile.Fallbacks)
                {
                    pending.Enqueue(fallback);
                }
            }
        }

        scope.AddRange(profiles.Select(profile => SettingPath.Child("providers", profile.Provider)).Distinct());
        scope.AddRange(Options.Capabilities
            .Where(entry => entry.Value.Enabled && (agent.Capabilities is null || agent.Capabilities.Contains(entry.Key)))
            .Select(entry => SettingPath.Child("capabilities", entry.Key)));
        return scope;
    }

    private void Flatten(JsonNode node, string path, List<EffectiveSetting> settings)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var (key, value) in obj)
            {
                Flatten(value!, SettingPath.Child(path, key), settings);
            }

            return;
        }

        settings.Add(new EffectiveSetting(path, node.ToJsonString(), origins.OriginOf(path)));
    }
}
