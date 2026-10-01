using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Hosting.Documentation;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Checks the shape of configuration JSON against the schema generated from the Options classes (phase 2): unknown
/// settings, wrong types, values outside the allowed ones, and, for the merged configuration, required settings.
/// Every problem is found in one pass, with its location; the bad values are removed so the rest can be merged and bound.
/// </summary>
internal sealed class ShapeValidator
{
    // Ranges are single-value rules of the Options classes, checked by the core for both forms; the schema has them for editors.
    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
    {
        "properties", "additionalProperties", "items", "minimum", "exclusiveMinimum",
    };

    private readonly JsonObject schemaNode;
    private readonly JsonSchema schema;

    private ShapeValidator(JsonObject schemaNode)
    {
        // Without an id, the schema is not registered globally, so both variants can exist side by side.
        schemaNode.Remove("$id");
        this.schemaNode = schemaNode;
        schema = JsonSchema.FromText(schemaNode.ToJsonString());
    }

    /// <summary>For one layer, which may set only some settings.</summary>
    public static ShapeValidator ForLayers { get; } = new(SchemaGenerator.Generate());

    /// <summary>For the merged configuration, in which required settings must be set.</summary>
    public static ShapeValidator ForMerged { get; } = new(SchemaGenerator.Generate(keepRequired: true));

    /// <summary>Reports each problem and removes the value at fault from <paramref name="root"/>.</summary>
    /// <param name="root">The configuration JSON.</param>
    /// <param name="locate">Where a setting was written.</param>
    /// <param name="errors">Where the problems are reported.</param>
    /// <param name="unknownSettingHint">Added to the fix of an unknown setting, for example about the case of variable names.</param>
    public void Check(JsonObject root, Func<string, ConfigurationOrigin?> locate, LoadErrors errors, string unknownSettingHint = "")
    {
        var results = schema.Evaluate(JsonSerializer.SerializeToElement(root), new EvaluationOptions { OutputFormat = OutputFormat.List });
        var faults = new List<string[]>();
        foreach (var detail in results.Details ?? [results])
        {
            foreach (var keyword in (detail.Errors ?? new Dictionary<string, string>()).Keys.Where(keyword => !Ignored.Contains(keyword)))
            {
                var names = Names(detail.InstanceLocation.ToString());
                var path = PathOf(root, names);
                var expected = At(schemaNode, Names(detail.EvaluationPath.ToString()));
                var instance = At(root, names);
                faults.Add(names);
                if (errors.IsRejected(path))
                {
                    continue;
                }

                if (keyword == "required")
                {
                    foreach (var missing in Missing(expected, instance).Where(missing => !errors.IsRejected(SettingPaths.Join(path, missing))))
                    {
                        var missingPath = SettingPaths.Join(path, missing);
                        errors.Add(ValidationPhase.Shape, missingPath, "is required but not set.", "Add it; it has no default.", locate(path));
                    }
                }
                else
                {
                    var (problem, fix) = Describe(keyword, expected, instance, names.LastOrDefault() ?? "", unknownSettingHint);
                    errors.Add(ValidationPhase.Shape, path, problem, fix, locate(path));
                }
            }
        }

        foreach (var names in faults.Where(names => names.Length > 0).OrderByDescending(names => names.Length))
        {
            Remove(root, names);
        }
    }

    private static (string Problem, string Fix) Describe(string keyword, JsonNode? expected, JsonNode? instance, string name, string hint)
    {
        if (keyword.Length == 0)
        {
            return ($"\"{name}\" is not a setting here.", "Remove it, or check the spelling against docs/configuration-settings.md." + hint);
        }

        if (instance is null)
        {
            return ("cannot be removed with null.", "Set a value; it may be high, but it always exists. Leave the setting out to use the default.");
        }

        if (expected?["properties"]?["secret"] is not null)
        {
            // The value is not repeated: it may be the secret itself.
            return ("is not a secret reference.",
                "Write { \"secret\": \"NAME\" } and put the value in the secret source, such as an environment variable NAME.");
        }

        var wanted = Expected(expected);
        return ($"is {Kind(instance)}, but must be {wanted}.", $"Write {wanted}.");
    }

    private static string Expected(JsonNode? schema)
    {
        if (schema?["enum"] is JsonArray choices)
        {
            return "one of " + string.Join(", ", choices.Where(choice => choice is not null).Select(choice => choice!.ToJsonString()));
        }

        var types = schema?["type"] switch
        {
            JsonArray list => list.Select(type => (string)type!).Where(type => type != "null"),
            JsonValue single => [(string)single!],
            _ => [],
        };
        return string.Join(" or ", types.Select(type => type switch
        {
            "string" when schema!["pattern"] is not null => "a duration: a number with a unit, ms, s, m, h or d, such as \"30m\"",
            "string" => "text",
            "integer" => "a whole number",
            "number" => "a number",
            "boolean" => "true or false",
            "array" => "a list",
            _ => "an object",
        }));
    }

    private static string Kind(JsonNode instance) => instance.GetValueKind() switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        _ => "true or false",
    };

    private static IEnumerable<string> Missing(JsonNode? schema, JsonNode? instance) =>
        (schema?["required"] as JsonArray ?? []).Select(name => (string)name!).Where(name => (instance as JsonObject)?.ContainsKey(name) != true);

    /// <summary>The setting path of a JSON pointer: names joined by dots, list positions in brackets.</summary>
    private static string PathOf(JsonNode root, string[] names)
    {
        var path = "";
        JsonNode? node = root;
        foreach (var name in names)
        {
            path = node is JsonArray ? $"{path}[{name}]" : SettingPaths.Join(path, name);
            node = At(node, [name]);
        }

        return path;
    }

    private static JsonNode? At(JsonNode? node, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            node = node switch
            {
                JsonObject obj => obj[name],
                JsonArray array when int.TryParse(name, out var index) && index < array.Count => array[index],
                _ => null,
            };
        }

        return node;
    }

    private static void Remove(JsonNode root, string[] names)
    {
        switch (At(root, names[..^1]))
        {
            case JsonObject parent:
                parent.Remove(names[^1]);
                break;
            case JsonArray list when int.TryParse(names[^1], out var index) && index < list.Count:
                list.RemoveAt(index);
                break;
        }
    }

    private static string[] Names(string pointer) =>
        [.. pointer.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))];
}
