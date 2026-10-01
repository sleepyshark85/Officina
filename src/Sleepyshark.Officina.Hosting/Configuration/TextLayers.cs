using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Layers from settings given as text: <c>SOF__run__budget__cost=8</c> environment variables, and run options such
/// as <c>run.budget.cost</c>. A value is JSON where it parses as JSON, otherwise text (§13).
/// </summary>
internal static class TextLayers
{
    private const string Prefix = "SOF__";

    public static Layer? FromEnvironment(IReadOnlyDictionary<string, string> variables, ConfigurationErrors errors) =>
        Build("environment variables", LayerKind.EnvironmentVariable,
            variables.Where(variable => variable.Key.StartsWith(Prefix, StringComparison.Ordinal))
                .OrderBy(variable => variable.Key, StringComparer.Ordinal)
                .Select(variable => (variable.Key[Prefix.Length..].Split("__"), variable.Value, variable.Key)),
            errors);

    public static Layer? FromRunOptions(IEnumerable<RunOption> options, ConfigurationErrors errors) =>
        Build("run options", LayerKind.RunOption, options.Select(option => (option.Path.Split('.'), option.Value, option.Source)), errors);

    private static Layer? Build(string description, LayerKind kind, IEnumerable<(string[] Names, string Value, string Source)> settings, ConfigurationErrors errors)
    {
        var root = new JsonObject();
        var positions = new Dictionary<string, ConfigOrigin>(StringComparer.Ordinal);
        foreach (var (names, value, source) in settings)
        {
            var origin = new ConfigOrigin(kind, source);
            if (!TryPlace(root, names, ParseOrText(value)))
            {
                errors.Add(ValidationPhase.Shape, "", $"\"{source}\" does not name a single setting.",
                    "Separate setting names with \"__\" in variables and \".\" in run options, and set each value once.", origin);
                continue;
            }

            positions[string.Join('.', names)] = origin;
        }

        return positions.Count == 0 ? null : new Layer(description, new ConfigOrigin(kind), root, positions);
    }

    private static bool TryPlace(JsonObject root, string[] names, JsonNode? value)
    {
        if (names.Any(name => name.Length == 0))
        {
            return false;
        }

        var target = root;
        foreach (var name in names[..^1])
        {
            if (!target.ContainsKey(name))
            {
                target[name] = new JsonObject();
            }

            if (target[name] is not JsonObject next)
            {
                return false;
            }

            target = next;
        }

        if (target.ContainsKey(names[^1]))
        {
            return false;
        }

        target[names[^1]] = value;
        return true;
    }

    private static JsonNode? ParseOrText(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }
}
