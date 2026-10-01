using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Reads layers from settings given as text: <c>SOF__run__budget__cost=8</c> environment variables, and run options
/// such as <c>run.budget.cost</c>. A value is JSON where it parses as JSON, otherwise text (§13).
/// </summary>
internal static class TextLayerReader
{
    private const string Prefix = "SOF__";

    private static readonly JsonDocumentOptions Strict = new() { AllowDuplicateProperties = false };

    public static Layer? FromEnvironment(IReadOnlyDictionary<string, string> variables, LoadErrors errors) =>
        Read("environment variables", LayerKind.EnvironmentVariable,
            variables.Where(variable => variable.Key.StartsWith(Prefix, StringComparison.Ordinal))
                .OrderBy(variable => variable.Key, StringComparer.Ordinal)
                .Select(variable => (variable.Key[Prefix.Length..].Split("__"), variable.Value, variable.Key)),
            errors);

    public static Layer? FromRunOptions(IEnumerable<RunOption> options, LoadErrors errors) =>
        Read("run options", LayerKind.RunOption, options.Select(option => (option.Path.Split('.'), option.Value, option.Source)), errors);

    private static Layer? Read(
        string description, LayerKind kind, IEnumerable<(string[] Names, string Value, string Source)> settings, LoadErrors errors)
    {
        var root = new JsonObject();
        var positions = new Dictionary<string, ConfigurationOrigin>(StringComparer.Ordinal);
        foreach (var (names, value, source) in settings)
        {
            var origin = new ConfigurationOrigin(kind, source);
            if (!TryParse(value, out var node))
            {
                errors.Add(ValidationPhase.Shape, string.Join('.', names), "has a key twice in the same object.", "Keep one of them.", origin);
            }
            else if (!TryPlace(root, names, node))
            {
                errors.Add(ValidationPhase.Shape, "", $"\"{source}\" does not name a single setting.",
                    "Separate setting names with \"__\" in variables and \".\" in run options, and set each value once.", origin);
            }
            else
            {
                positions[string.Join('.', names)] = origin;
            }
        }

        return positions.Count == 0 ? null : new Layer(description, new ConfigurationOrigin(kind), root, positions);
    }

    /// <summary>The value as JSON where it is JSON, otherwise as text; false when it is JSON with a duplicate key.</summary>
    private static bool TryParse(string value, out JsonNode? node)
    {
        if (!IsJson(value))
        {
            node = JsonValue.Create(value);
            return true;
        }

        try
        {
            node = JsonNode.Parse(value, documentOptions: Strict);
            return true;
        }
        catch (JsonException)
        {
            node = null;
            return false;
        }
    }

    private static bool IsJson(string value)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value));
        try
        {
            while (reader.Read())
            {
            }

            return reader.BytesConsumed > 0;
        }
        catch (JsonException)
        {
            return false;
        }
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
}
