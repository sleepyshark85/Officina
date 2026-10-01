using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>A loaded configuration: the bound Options, every validation error, and the origin of every value.</summary>
public sealed class LoadedConfiguration
{
    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The top-level sections that are maps of named entries, such as <c>agents</c>.</summary>
    private static readonly string[] NamedSections = [.. typeof(OfficinaOptions).GetProperties()
        .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
        .Select(property => JsonNamingPolicy.CamelCase.ConvertName(property.Name))];

    private readonly Func<string, ConfigurationOrigin> originOf;

    internal LoadedConfiguration(
        OfficinaOptions options, IReadOnlyList<ConfigurationError> errors, Func<string, ConfigurationOrigin> originOf, IReadOnlyList<string> layers)
    {
        Options = options;
        Errors = errors;
        this.originOf = originOf;
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

    /// <summary>Where a value came from: the highest layer that sets it, or the code default.</summary>
    /// <param name="path">The setting, such as <c>run.budget.cost</c>.</param>
    public ConfigurationOrigin OriginOf(string path) => originOf(path);

    /// <summary>
    /// Every effective setting with its origin. For an agent, the ones that apply to it: everything outside the named
    /// sections, and of those only the agent itself, its model profile and that profile's provider.
    /// </summary>
    /// <exception cref="ConfigurationException">There is no such agent.</exception>
    public IReadOnlyList<EffectiveSetting> Settings(string? agent = null)
    {
        var settings = new List<EffectiveSetting>();
        Flatten(JsonSerializer.SerializeToNode(Options, ConfigurationJson.Options)!, "", settings);
        if (agent is null)
        {
            return settings;
        }

        if (!Options.Agents.TryGetValue(agent, out var definition))
        {
            throw ConfigurationException.UnknownAgent(agent, Options.Agents.Keys);
        }

        var provider = Options.Models.TryGetValue(definition.Model, out var profile) ? profile.Provider : "";
        string[] used = [$"agents.{agent}", $"models.{definition.Model}", $"providers.{provider}"];
        bool Applies(string path) =>
            !NamedSections.Any(section => SettingPaths.IsWithin(path, section)) || used.Any(entry => SettingPaths.IsWithin(path, entry));
        return [.. settings.Where(setting => Applies(setting.Path))];
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
