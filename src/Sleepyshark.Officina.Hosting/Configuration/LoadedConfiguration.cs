using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>One effective setting: its path, its value as JSON, and where it came from (CFG-04).</summary>
public sealed record EffectiveSetting(string Path, string Value, ConfigOrigin Origin);

/// <summary>A loaded configuration: the bound Options, every validation error, and the origin of every value.</summary>
public sealed class LoadedConfiguration
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IReadOnlyDictionary<string, ConfigOrigin> origins;

    internal LoadedConfiguration(OfficinaOptions options, IReadOnlyList<ConfigurationError> errors, IReadOnlyDictionary<string, ConfigOrigin> origins, IReadOnlyList<string> layers)
    {
        Options = options;
        Errors = errors;
        this.origins = origins;
        Layers = layers;
    }

    /// <summary>The effective configuration. Use it only when <see cref="IsValid"/>.</summary>
    public OfficinaOptions Options { get; }

    public IReadOnlyList<ConfigurationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    /// <summary>The layers that were read above the code defaults, lowest first.</summary>
    public IReadOnlyList<string> Layers { get; }

    /// <exception cref="ConfigurationException">The configuration has errors.</exception>
    public OfficinaOptions ValidOptions() => IsValid ? Options : throw new ConfigurationException(Errors);

    /// <summary>Where a value came from; a value no layer set is a code default.</summary>
    public ConfigOrigin OriginOf(string path) => origins.GetValueOrDefault(path) ?? ConfigOrigin.CodeDefault;

    /// <summary>
    /// Every effective setting with its origin; for an agent, the ones that apply to it: the project, its
    /// definition, its model profile and provider, and the run defaults.
    /// </summary>
    /// <exception cref="ArgumentException">There is no such agent.</exception>
    public IReadOnlyList<EffectiveSetting> Settings(string? agent = null)
    {
        var settings = new List<EffectiveSetting>();
        Flatten(JsonSerializer.SerializeToNode(Options, OfficinaJson.Options)!, "", settings);
        if (agent is null)
        {
            return settings;
        }

        if (!Options.Agents.TryGetValue(agent, out var definition))
        {
            throw new ArgumentException($"There is no agent \"{agent}\". Agents: {(Options.Agents.Count == 0 ? "none" : string.Join(", ", Options.Agents.Keys))}.", nameof(agent));
        }

        var provider = Options.Models.TryGetValue(definition.Model, out var profile) ? profile.Provider : "";
        string[] scope = ["project", $"agents.{agent}", $"models.{definition.Model}", $"providers.{provider}", "run"];
        return [.. settings.Where(setting => scope.Any(prefix => setting.Path == prefix || setting.Path.StartsWith(prefix + ".", StringComparison.Ordinal)))];
    }

    private void Flatten(JsonNode node, string path, List<EffectiveSetting> settings)
    {
        if (node is JsonObject obj && obj.Count > 0)
        {
            foreach (var (key, value) in obj)
            {
                Flatten(value!, SettingPaths.Join(path, key), settings);
            }

            return;
        }

        settings.Add(new EffectiveSetting(path, node.ToJsonString(Compact), OriginOf(path)));
    }
}
