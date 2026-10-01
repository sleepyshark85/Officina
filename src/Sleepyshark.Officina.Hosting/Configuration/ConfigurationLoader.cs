using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Loads a configuration from its layers, lowest to highest: code defaults, extended files, <c>sof.json</c>,
/// <c>sof.&lt;environment&gt;.json</c>, <c>SOF__…</c> environment variables and run options (CFG-04). Each layer is
/// checked against the schema, merged, and the result is checked, bound and validated in full (CFG-06). Every load
/// reads the files again, so a changed file applies to the next run without a rebuild (CFG-08).
/// </summary>
public static class ConfigurationLoader
{
    private const string CaseHint = " Setting names are case-sensitive, as in SOF__run__permissionMode.";

    public static LoadedConfiguration Load(ConfigurationSources sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var errors = new LoadErrors();
        var origins = new OriginMap();
        var layers = ReadLayers(sources, errors);

        var merger = new LayerMerger(JsonSerializer.SerializeToNode(new OfficinaOptions(), ConfigurationJson.Options)!.AsObject(), origins);
        foreach (var layer in layers)
        {
            var hint = layer.Origin.Layer == LayerKind.EnvironmentVariable ? CaseHint : "";
            ShapeValidator.ForLayers.Check(layer.Root, layer.OriginOf, errors, hint);
            merger.Apply(layer);
        }

        new DefinitionExtends(merger, origins, errors).Resolve(merger.Merged);
        LayerMerger.RemoveNulls(merger.Merged);
        ShapeValidator.ForMerged.Check(merger.Merged, origins.LocationOf, errors);
        var options = OptionsBinder.Bind(merger.Merged, errors);

        // Rules that also apply to the programmatic form; a setting already rejected is not reported again.
        var semantic = ConfigurationValidator.Validate(options)
            .Where(error => !errors.IsRejected(error.Path))
            .Select(error => error with { Location = origins.LocationOf(error.Path)?.Location });
        var all = errors.All.Concat(semantic).OrderBy(error => error.Phase).ToArray();
        return new LoadedConfiguration(options, all, origins.Values, [.. layers.Select(layer => layer.Description)]);
    }

    private static List<Layer> ReadLayers(ConfigurationSources sources, LoadErrors errors)
    {
        var files = new FileLayerReader(Path.GetFullPath(sources.Directory), errors);
        var layers = files.Read(ConfigurationSources.MainFile, LayerKind.ApplicationFile);
        if (sources.Environment is { Length: > 0 } environment)
        {
            layers.AddRange(files.Read($"sof.{environment}.json", LayerKind.EnvironmentFile));
        }

        if (TextLayerReader.FromEnvironment(sources.EnvironmentVariables, errors) is { } variables)
        {
            layers.Add(variables);
        }

        if (TextLayerReader.FromRunOptions(sources.RunOptions, errors) is { } runOptions)
        {
            layers.Add(runOptions);
        }

        return layers;
    }
}
