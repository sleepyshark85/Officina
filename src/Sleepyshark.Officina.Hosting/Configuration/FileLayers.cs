using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Reads configuration files as layers: a file, preceded by the files it extends, in order (§13). Checks the
/// syntax, the format version and <c>extends</c> cycles.
/// </summary>
internal sealed class FileLayers(string directory, ConfigurationErrors errors)
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The layers of the file, lowest first; none when the file does not exist.</summary>
    public List<Layer> Read(string relativePath, LayerKind kind)
    {
        var layers = new List<Layer>();
        Read(Path.GetFullPath(relativePath, directory), kind, [], null, layers);
        return layers;
    }

    private void Read(string fullPath, LayerKind kind, List<string> chain, ConfigOrigin? namedAt, List<Layer> layers)
    {
        var source = Display(fullPath);
        if (chain.Contains(fullPath))
        {
            errors.Add(ValidationPhase.Merge, "extends", $"extends forms a cycle: {string.Join(" → ", chain.SkipWhile(item => item != fullPath).Append(fullPath).Select(Display))}.",
                "Remove one of the extends entries.", namedAt);
            return;
        }

        if (!File.Exists(fullPath))
        {
            if (namedAt is not null)
            {
                errors.Add(ValidationPhase.Merge, "extends", $"file \"{source}\" does not exist.", "Paths are relative to the file that names them.", namedAt);
            }

            return;
        }

        var text = File.ReadAllText(fullPath);
        var origin = new ConfigOrigin(kind, source);
        if (JsonPositions.Read(text, origin, errors) is not { } positions)
        {
            return;
        }

        if (JsonNode.Parse(text, documentOptions: DocumentOptions) is not JsonObject root)
        {
            errors.Add(ValidationPhase.Parse, "", "the file is not a JSON object.", "Put the settings in { … }.", origin with { Line = 1, Column = 1 });
            return;
        }

        var layer = new Layer(origin.ToString(), origin, root,
            positions.ToDictionary(entry => entry.Key, entry => origin with { Line = entry.Value.Line, Column = entry.Value.Column }, StringComparer.Ordinal));
        if (root["formatVersion"] is { } version && version.ToJsonString() != OfficinaOptions.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture))
        {
            errors.Add(ValidationPhase.Parse, "formatVersion", $"format version {version.ToJsonString()} is not supported.",
                $"This core reads format version {OfficinaOptions.CurrentFormatVersion}.", layer.OriginOf("formatVersion"));
            return;
        }

        ReadExtended(root, fullPath, layer, [.. chain, fullPath], layers);
        root.Remove("extends");
        root.Remove("$schema");
        layers.Add(layer);
    }

    private void ReadExtended(JsonObject root, string fullPath, Layer layer, List<string> chain, List<Layer> layers)
    {
        if (root["extends"] is null)
        {
            return;
        }

        if (root["extends"] is not JsonArray extends)
        {
            errors.Add(ValidationPhase.Shape, "extends", "is not a list.", "Write a list of file paths, such as [\"base.json\"].", layer.OriginOf("extends"));
            return;
        }

        foreach (var (entry, index) in extends.Select((entry, index) => (entry, index)))
        {
            var at = layer.OriginOf($"extends[{index}]");
            if (entry?.GetValueKind() != JsonValueKind.String || entry.GetValue<string>().StartsWith("preset:", StringComparison.Ordinal))
            {
                errors.Add(ValidationPhase.Merge, $"extends[{index}]", "is not a file path.", "Name a file, relative to this one. Presets arrive with the coding team (S20).", at);
                continue;
            }

            Read(Path.GetFullPath(entry.GetValue<string>(), Path.GetDirectoryName(fullPath)!), LayerKind.ExtendedFile, chain, at, layers);
        }
    }

    private string Display(string fullPath) => Path.GetRelativePath(directory, fullPath).Replace('\\', '/');
}
