using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.FileProviders;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// The configuration providers, lowest to highest: <c>sof.json</c>, <c>sof.&lt;environment&gt;.json</c>, <c>SOF__…</c>
/// environment variables and run options (§13), each with where its values come from. Code defaults are the
/// initial values of the Options classes, below every provider.
/// </summary>
internal sealed class ConfigurationLayers
{
    private const string VariablePrefix = "SOF__";

    private readonly List<(IConfigurationSource Source, string Description, Func<string, ConfigurationOrigin> Origin)> layers = [];

    public ConfigurationLayers(ConfigurationSources sources, List<ConfigurationError> errors)
    {
        var files = new PhysicalFileProvider(Path.GetFullPath(sources.Directory));
        AddFile(files, ConfigurationSources.MainFile, LayerKind.ApplicationFile, errors);
        if (sources.Environment is { Length: > 0 } environment)
        {
            AddFile(files, $"sof.{environment}.json", LayerKind.EnvironmentFile, errors);
        }

        // The variables are passed in rather than read from the process, so a host or a test decides what they are.
        AddText(LayerKind.EnvironmentVariable, sources.EnvironmentVariables
            .Where(variable => variable.Key.StartsWith(VariablePrefix, StringComparison.Ordinal))
            .Select(variable => (variable.Key[VariablePrefix.Length..].Replace("__", ":", StringComparison.Ordinal), variable.Value, variable.Key)));
        AddText(LayerKind.RunOption, sources.RunOptions.Select(option => (option.Path.Replace('.', ':'), option.Value, option.Source)));
    }

    /// <summary>The configuration, with the values agents inherit through <c>extends</c> as the lowest provider.</summary>
    public IConfigurationRoot Build(IReadOnlyDictionary<string, string?>? inherited = null)
    {
        var builder = new ConfigurationBuilder();
        builder.Add(new MemoryConfigurationSource { InitialData = inherited ?? new Dictionary<string, string?>() });
        foreach (var (source, _, _) in layers)
        {
            builder.Add(source);
        }

        return builder.Build();
    }

    /// <summary>The descriptions of the layers that were read, lowest first.</summary>
    public IReadOnlyList<string> Descriptions => [.. layers.Select(layer => layer.Description)];

    /// <summary>The origin of a key or section: the highest layer that sets it, or null when only code defaults do.</summary>
    /// <param name="root">A configuration from <see cref="Build"/>, whose first provider holds the inherited values.</param>
    /// <param name="key">The key, such as <c>run:budget:cost</c>.</param>
    public ConfigurationOrigin? OriginOf(IConfigurationRoot root, string key)
    {
        var providers = root.Providers.ToArray();
        for (var index = providers.Length - 1; index >= 1; index--)
        {
            if (providers[index].TryGet(key, out _) || providers[index].GetChildKeys([], key).Any())
            {
                return layers[index - 1].Origin(key);
            }
        }

        return null;
    }

    private void AddFile(PhysicalFileProvider files, string name, LayerKind kind, List<ConfigurationError> errors)
    {
        var source = new JsonConfigurationSource { FileProvider = files, Path = name, Optional = true };
        try
        {
            new ConfigurationBuilder().Add(source).Build();
        }
        catch (InvalidDataException exception)
        {
            var reason = exception.InnerException?.Message ?? exception.Message;
            errors.Add(new ConfigurationError(ValidationPhase.Parse, "", $"the file cannot be read: {reason}",
                "Fix the JSON. Comments (//, /* */) and trailing commas are allowed.") { Location = name });
            return;
        }

        if (files.GetFileInfo(name).Exists)
        {
            var origin = new ConfigurationOrigin(kind, name);
            layers.Add((source, origin.ToString(), _ => origin));
        }
    }

    private void AddText(LayerKind kind, IEnumerable<(string Key, string Value, string Source)> settings)
    {
        var values = settings.ToArray();
        if (values.Length == 0)
        {
            return;
        }

        var sourceOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            sourceOf[value.Key] = value.Source;
        }

        var data = values.Select(value => KeyValuePair.Create(value.Key, (string?)value.Value));
        var description = kind == LayerKind.EnvironmentVariable ? "environment variables" : "run options";
        var source = new MemoryConfigurationSource { InitialData = data };
        layers.Add((source, description, key => new ConfigurationOrigin(kind, Source(sourceOf, key, description))));
    }

    /// <summary>The variable or option that set a key; for a section, the first one beneath it.</summary>
    private static string Source(Dictionary<string, string> sourceOf, string key, string description) =>
        sourceOf.GetValueOrDefault(key)
        ?? sourceOf.FirstOrDefault(entry => entry.Key.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)).Value
        ?? description;
}
