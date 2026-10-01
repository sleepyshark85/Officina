using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Core.Conditions;

/// <summary>
/// The field roots a condition may read where it is used, with the JSON Schema of each, so validation
/// can check every path (configuration reference §6). A root holding free text cannot be read (INV-01).
/// </summary>
public sealed class ConditionScope
{
    private readonly Dictionary<string, FieldSource> roots = new(StringComparer.Ordinal);

    public IEnumerable<string> Roots => roots.Keys.Order(StringComparer.Ordinal);

    /// <summary>The schema of a check result as conditions see it: whether it passed, and how many findings it has.</summary>
    public static JsonElement CheckResultSchema { get; } = Schema("""
        { "type": "object", "properties": { "passed": { "type": "boolean" }, "findings": { "type": "integer" } }, "additionalProperties": false }
        """);

    /// <summary>The schema of a step outcome.</summary>
    public static JsonElement OutcomeSchema { get; } = Schema("""{ "type": "string", "enum": ["completed", "handedOff", "failed", "cancelled"] }""");

    /// <summary>Adds a root of structured values. A null schema means the shape is not known, so paths under it are not checked.</summary>
    public ConditionScope Structured(string root, JsonElement? schema)
    {
        roots[root] = new FieldSource(schema, FreeText: false);
        return this;
    }

    /// <summary>Adds a root that holds free text, such as the output of a step with text output. Conditions on it are rejected (INV-01).</summary>
    public ConditionScope FreeText(string root)
    {
        roots[root] = new FieldSource(null, FreeText: true);
        return this;
    }

    /// <summary>Adds the <c>checks</c> root for the named checks.</summary>
    public ConditionScope Checks(IEnumerable<string> names)
    {
        var properties = new JsonObject();
        foreach (var name in names)
        {
            properties[name] = JsonNode.Parse(CheckResultSchema.GetRawText());
        }

        return Structured("checks", Schema(new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false }.ToJsonString()));
    }

    internal FieldSource? Find(string root) => roots.GetValueOrDefault(root);

    public static JsonElement Schema(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal sealed record FieldSource(JsonElement? Schema, bool FreeText);
}
