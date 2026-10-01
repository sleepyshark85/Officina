using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Loads a configuration from its layers, lowest to highest: code defaults, extended files, <c>sof.json</c>,
/// <c>sof.&lt;environment&gt;.json</c>, <c>SOF__…</c> environment variables and run options (CFG-04). It merges them,
/// resolves <c>extends</c> between definitions, binds the result to the Options classes and validates it in full
/// (CFG-06). Every load reads the files again, so a changed file applies to the next run without a rebuild (CFG-08).
/// </summary>
public static class ConfigurationLoader
{
    public static LoadedConfiguration Load(ConfigurationSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var errors = new ConfigurationErrors();
        var origins = new OriginMap();
        var merger = new LayerMerger(JsonSerializer.SerializeToNode(new OfficinaOptions(), OfficinaJson.Options)!.AsObject(), origins, errors);

        var files = new FileLayers(Path.GetFullPath(sources.Directory), errors);
        var layers = files.Read(ConfigurationSources.MainFile, LayerKind.ApplicationFile);
        if (sources.Environment is { Length: > 0 } environment)
        {
            layers.AddRange(files.Read($"sof.{environment}.json", LayerKind.EnvironmentFile));
        }

        layers.AddRange(new[] { TextLayers.FromEnvironment(sources.EnvironmentVariables, errors), TextLayers.FromRunOptions(sources.RunOptions, errors) }.OfType<Layer>());
        foreach (var layer in layers)
        {
            merger.Apply(layer);
        }

        new DefinitionExtends(merger, origins, errors).Resolve(merger.Merged);
        LayerMerger.RemoveNulls(merger.Merged);
        var options = OptionsBinder.Bind(merger.Merged, origins, errors);

        // Rules that also apply to the programmatic form; a setting already rejected is not reported again.
        var semantic = ConfigurationValidator.Validate(options)
            .Where(error => !errors.IsRejected(error.Path))
            .Select(error => error with { Location = origins.LocationOf(error.Path)?.Location });
        return new LoadedConfiguration(options, [.. errors.All.Concat(semantic).OrderBy(error => error.Phase)], origins.Values, [.. layers.Select(layer => layer.Description)]);
    }
}
