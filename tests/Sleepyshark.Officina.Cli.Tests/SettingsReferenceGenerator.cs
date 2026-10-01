using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Generates the settings reference from the JSON Schema, so it lists every setting with its meaning, allowed
/// values, default and an example, and cannot drift from the Options classes (DOC-01).
/// </summary>
internal static class SettingsReferenceGenerator
{
    public static string Generate(JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var text = new StringBuilder("""
            # Officina — Settings reference

            Generated from the Options classes by the test that keeps this file current; do not edit it by hand.
            It lists the settings the code has today. Settings that later slices add are specified in the draft
            [configuration reference](configuration-reference.md), which also describes layering (§13) and validation (§14).

            """);
        var sections = new Queue<(string Title, JsonObject Schema)>([("Top level", schema)]);
        var listed = new HashSet<string>();
        while (sections.TryDequeue(out var section))
        {
            // A type used in several places, such as a condition, is listed where it first appears.
            if (!listed.Add(string.Join(",", ((JsonObject)section.Schema["properties"]!).Select(property => property.Key))))
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"\n## {section.Title}\n\n");
            text.Append("| Setting | Allowed values | Default | Description | Example |\n|---|---|---|---|---|\n");
            var prefix = section.Title == "Top level" ? "" : section.Title.Trim('`') + ".";
            foreach (var (name, value) in (JsonObject)section.Schema["properties"]!)
            {
                var described = (JsonObject)value!;
                var property = Resolve(schema, described);
                var description = Escape((string?)described["description"] ?? "");
                text.Append(CultureInfo.InvariantCulture,
                    $"| `{name}` | {Allowed(property)} | {Code(described["default"])} | {description} | {Code(described["examples"]?[0])} |\n");
                if (described["properties"] is JsonObject)
                {
                    sections.Enqueue(($"`{prefix}{name}`", property));
                }
                else if (property["additionalProperties"] is JsonObject { } entry && entry["properties"] is JsonObject)
                {
                    sections.Enqueue(($"`{prefix}{name}.<name>`", entry));
                }
                else if (described["items"] is JsonObject { } item && item["properties"] is JsonObject)
                {
                    sections.Enqueue(($"`{prefix}{name}[]`", item));
                }
            }
        }

        // Raw string literals take the line endings of the checkout; the reference always has \n.
        return text.ToString().ReplaceLineEndings("\n");
    }

    /// <summary>The schema a <c>$ref</c> points to, for a type that appears more than once; otherwise the property itself.</summary>
    private static JsonObject Resolve(JsonObject root, JsonObject property) =>
        property["$ref"] is JsonValue reference
            ? (JsonObject)((string)reference!).TrimStart('#', '/').Split('/').Aggregate((JsonNode)root, (node, key) => node[key]!)
            : property;

    private static string Allowed(JsonObject property)
    {
        if (property["enum"] is JsonArray choices)
        {
            return string.Join(", ", choices.Where(choice => choice is not null).Select(choice => $"`{choice!.ToJsonString()}`"));
        }

        var types = property["type"] switch
        {
            JsonArray list => list.Select(type => (string)type!).Where(type => type != "null").ToArray(),
            JsonValue single => [(string)single!],
            _ => [],
        };
        var allowed = string.Join(" or ", types.Select(type => type switch
        {
            "object" when property["additionalProperties"] is JsonObject => "named entries",
            "object" => "section",
            "array" => "list",
            "string" when property["pattern"] is not null => "time span (`hh:mm:ss` or `d.hh:mm:ss`)",
            "string" => "text",
            "integer" => "whole number",
            _ => type,
        }));
        if (property["exclusiveMinimum"] is { } above)
        {
            allowed += $", > {above}";
        }
        else if (property["minimum"] is { } least)
        {
            allowed += $", ≥ {least}";
        }

        if (property["exclusiveMaximum"] is { } below)
        {
            allowed += $", < {below}";
        }
        else if (property["maximum"] is { } most)
        {
            allowed += $", ≤ {most}";
        }

        return allowed.Length == 0 ? "any JSON value" : allowed;
    }

    private static string Code(JsonNode? value) =>
        value is null ? "" : "`" + value.ToJsonString().Replace("|", "\\|", StringComparison.Ordinal) + "`";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
