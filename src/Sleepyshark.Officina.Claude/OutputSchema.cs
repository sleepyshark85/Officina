using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Adjusts a typed output schema to what Claude's structured output accepts: every object closed with
/// <c>additionalProperties: false</c>, and numeric bounds, <c>maxItems</c> and a <c>minItems</c> above 1 left out. The
/// core still validates the reply against the schema as written. An open object, such as a dictionary, cannot be closed
/// without changing its meaning, so it is refused. Not probed live: an <c>enum</c> holding <c>null</c>, and a
/// <c>true</c> schema.
/// </summary>
internal static class OutputSchema
{
    private static readonly string[] Unsupported = ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf", "maxItems"];

    public static Dictionary<string, JsonElement> Adjust(string schema)
    {
        var root = JsonNode.Parse(schema)!.AsObject();
        Adjust(root);
        return root.ToDictionary(keyword => keyword.Key, keyword => JsonSerializer.SerializeToElement(keyword.Value));
    }

    /// <summary>Adjusts a schema in the core's subset, and the schemas inside it.</summary>
    private static void Adjust(JsonNode? node)
    {
        if (node is not JsonObject schema)
        {
            return;
        }

        foreach (var keyword in Unsupported)
        {
            schema.Remove(keyword);
        }

        if (schema["minItems"] is JsonValue minItems && minItems.GetValue<int>() > 1)
        {
            schema.Remove("minItems");
        }

        if (schema["additionalProperties"] is { } additional && additional.GetValueKind() != JsonValueKind.False)
        {
            throw new ArgumentException("The output schema has an open object, which structured output cannot express.");
        }

        var type = schema["type"];
        if (schema.ContainsKey("properties") || type is JsonValue { } single && single.GetValue<string>() == "object"
            || type is JsonArray types && types.Any(each => each?.GetValue<string>() == "object"))
        {
            schema["additionalProperties"] = false;
        }

        foreach (var property in schema["properties"] as JsonObject ?? [])
        {
            Adjust(property.Value);
        }

        Adjust(schema["items"]);
        foreach (var option in schema["anyOf"] as JsonArray ?? [])
        {
            Adjust(option);
        }
    }
}
