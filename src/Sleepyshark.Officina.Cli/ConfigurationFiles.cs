using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The configuration files and what they build on (CFG-05, CFG-11): a file's <c>extends</c> lists presets, as
/// <c>preset:&lt;id&gt;</c>, and other files, relative to it, each a layer below it; an agent's <c>extends</c> names another
/// agent it builds on. Layers merge as the configuration's layers do, objects key by key ignoring case, except that a list or a
/// value in a higher layer replaces the lower one's, so a list is never merged item by item with a preset's.
/// </summary>
internal static class ConfigurationFiles
{
    private const string Extends = "extends";

    private static readonly JsonNodeOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Adds a file to the layers, after the presets and files it extends, each once and in order.</summary>
    /// <param name="path">The file.</param>
    /// <param name="source">How the file is named to the owner.</param>
    /// <param name="layers">The layers so far, lowest first.</param>
    /// <param name="errors">Where a missing preset or a cycle of <c>extends</c> is reported (the merge phase).</param>
    /// <param name="loaded">The files and presets already among the layers, each of which is a layer once.</param>
    /// <exception cref="IOException">A file cannot be read.</exception>
    /// <exception cref="JsonException">A file is not JSON.</exception>
    public static void Add(string path, string source, List<(string Source, JsonObject Json)> layers, List<ConfigurationError> errors, HashSet<string> loaded) =>
        AddLayer(File.ReadAllText(path), source, Path.GetFullPath(path), layers, errors, [], loaded);

    /// <summary>The layers merged into one, lowest first.</summary>
    public static JsonObject Merge(IEnumerable<JsonObject> layers) => layers.Aggregate(new JsonObject(CaseInsensitive), (merged, layer) => (JsonObject)Merge(merged, layer)!);

    /// <summary>
    /// Builds each agent that extends another on it: the other's settings, resolved first, under its own. The <c>extends</c> keys
    /// are removed, so what is bound is the result. A missing agent or a cycle is reported.
    /// </summary>
    public static List<ConfigurationError> ExtendAgents(JsonObject merged)
    {
        var errors = new List<ConfigurationError>();
        if (merged["agents"] is not JsonObject agents)
        {
            return errors;
        }

        var resolved = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in agents.Select(agent => agent.Key).ToList())
        {
            Resolve(name, []);
        }

        foreach (var (name, agent) in resolved)
        {
            agents[name] = agent;
        }

        return errors;

        JsonObject? Resolve(string name, List<string> building)
        {
            if (resolved.TryGetValue(name, out var done))
            {
                return done;
            }

            if (agents[name] is not JsonObject agent)
            {
                return null;
            }

            var own = (JsonObject)agent.DeepClone();
            if (own[Extends] is not JsonValue reference || reference.GetValueKind() != JsonValueKind.String)
            {
                own.Remove(Extends);
                return resolved[name] = own;
            }

            var baseName = reference.GetValue<string>();
            own.Remove(Extends);
            if (building.Contains(baseName, StringComparer.OrdinalIgnoreCase) || string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new(ValidationPhase.Merge, $"agents.{name}.extends", $"builds on itself: {string.Join(" → ", building.Append(name).Append(baseName))}.", "Break the cycle of extends."));
                return resolved[name] = own;
            }

            if (!agents.ContainsKey(baseName))
            {
                errors.Add(new(ValidationPhase.Merge, $"agents.{name}.extends", $"agent \"{baseName}\" does not exist.", $"Add it to agents, or use one of: {string.Join(", ", agents.Select(agent => agent.Key).Order(StringComparer.Ordinal))}."));
                return resolved[name] = own;
            }

            var under = Resolve(baseName, [.. building, name]) ?? new JsonObject(CaseInsensitive);
            return resolved[name] = (JsonObject)Merge(under, own)!;
        }
    }

    /// <summary>The highest layer that sets a setting, by its configuration key such as <c>agents:dev:model</c>; one that an agent inherits is named as inherited.</summary>
    public static string SourceOf(IReadOnlyList<(string Source, JsonObject Json)> layers, string key)
    {
        var path = key.Split(':');
        for (var index = layers.Count - 1; index >= 0; index--)
        {
            if (Has(layers[index].Json, path))
            {
                return layers[index].Source;
            }
        }

        return path is ["agents", var agent, ..] ? $"inherited by agents.{agent} through extends" : "a configuration file";
    }

    /// <summary>Adds a file's or a preset's text as a layer, after what it extends.</summary>
    /// <param name="text">The JSON.</param>
    /// <param name="source">How it is named to the owner.</param>
    /// <param name="identity">The file's full path, or the preset's reference, which a cycle is found by.</param>
    /// <param name="layers">The layers so far, lowest first.</param>
    /// <param name="errors">Where a missing preset or a cycle is reported.</param>
    /// <param name="extending">What extends this one, the top file first.</param>
    /// <param name="loaded">What is among the layers already.</param>
    private static void AddLayer(
        string text, string source, string identity, List<(string Source, JsonObject Json)> layers, List<ConfigurationError> errors, IReadOnlyList<string> extending, HashSet<string> loaded)
    {
        var directory = identity.StartsWith("preset:", StringComparison.Ordinal) ? Path.GetDirectoryName(extending[0])! : Path.GetDirectoryName(identity)!;
        using (var document = JsonDocument.Parse(text, Lenient))
        {
            Unique(document.RootElement, source);
        }

        var json = JsonNode.Parse(text, CaseInsensitive, Lenient) as JsonObject ?? throw new JsonException($"{source} is not a JSON object.");
        var bases = json[Extends] switch
        {
            JsonArray list => list.Select(item => item?.GetValue<string>()).OfType<string>().ToList(),
            JsonValue one when one.GetValueKind() == JsonValueKind.String => [one.GetValue<string>()],
            _ => [],
        };
        json.Remove(Extends);
        foreach (var reference in bases)
        {
            var name = reference.StartsWith("preset:", StringComparison.Ordinal) ? reference : Path.GetFullPath(Path.Combine(directory, reference));
            if (extending.Contains(name) || name == identity)
            {
                errors.Add(new(ValidationPhase.Merge, Extends, $"{source} builds on itself through {reference}.", "Break the cycle of extends."));
                continue;
            }

            if (loaded.Contains(name))
            {
                continue; // already below, once
            }

            if (reference.StartsWith("preset:", StringComparison.Ordinal))
            {
                if (Presets.Read(reference["preset:".Length..]) is { } preset)
                {
                    AddLayer(preset, reference, reference, layers, errors, [.. extending, identity], loaded);
                }
                else
                {
                    errors.Add(new(ValidationPhase.Merge, Extends, $"preset \"{reference}\" does not exist.", $"Use one of: {string.Join(", ", Presets.Ids.Select(id => $"preset:{id}"))}."));
                }
            }
            else
            {
                AddLayer(File.ReadAllText(name), Path.GetRelativePath(Path.GetDirectoryName(extending.Count > 0 ? extending[0] : identity)!, name), name, layers, errors, [.. extending, identity], loaded);
            }
        }

        layers.Add((source, json));
        loaded.Add(identity);
    }

    /// <summary>A key set twice in one object, which names match ignoring case, is an error, as the configuration's own JSON files make it.</summary>
    private static void Unique(JsonElement element, string source)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                {
                    throw new JsonException($"{source} sets the key {property.Name} twice.");
                }

                Unique(property.Value, source);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                Unique(item, source);
            }
        }
    }

    private static JsonNode? Merge(JsonNode? lower, JsonNode? upper)
    {
        if (lower is not JsonObject below || upper is not JsonObject above)
        {
            return upper?.DeepClone();
        }

        var merged = new JsonObject(CaseInsensitive);
        foreach (var (key, value) in below)
        {
            merged[key] = value?.DeepClone();
        }

        foreach (var (key, value) in above)
        {
            merged[key] = Merge(merged[key], value);
        }

        return merged;
    }

    private static bool Has(JsonNode? node, ReadOnlySpan<string> path)
    {
        while (path.Length > 0)
        {
            node = node switch
            {
                JsonObject section => section.TryGetPropertyValue(path[0], out var child) ? child ?? JsonValue.Create(0) : null,
                JsonArray list when int.TryParse(path[0], out var index) && index < list.Count => list[index] ?? JsonValue.Create(0),
                _ => null,
            };
            if (node is null)
            {
                return false;
            }

            path = path[1..];
        }

        return true;
    }
}
