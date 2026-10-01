using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Documentation;

/// <summary>
/// Generates the JSON Schema of configuration files from the Options classes with the built-in exporter, adding the
/// descriptions, examples, defaults and ranges of <see cref="SettingAttribute"/> (CFG-15). A file is a layer that may
/// set only some settings, or remove one with <c>null</c>, so the published schema requires nothing; the variant for
/// checking the merged configuration keeps the required settings.
/// </summary>
public static class SchemaGenerator
{
    public const string SchemaId = "https://raw.githubusercontent.com/sleepyshark85/Officina/main/docs/officina.schema.json";

    /// <param name="keepRequired">Whether required settings stay required, for the merged configuration.</param>
    public static JsonObject Generate(bool keepRequired = false)
    {
        var exporter = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, schema) => Transform(context, schema, keepRequired),
        };
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(ConfigurationJson.Options, typeof(OfficinaOptions), exporter).AsObject();
        schema["type"] = "object";
        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = SchemaId,
            ["title"] = "Officina configuration",
            ["description"] = "Generated from the Options classes of Sleepyshark.Officina.Core; see docs/configuration-settings.md.",
        };
        foreach (var (key, value) in schema.ToArray())
        {
            schema.Remove(key);
            root[key] = value;
        }

        return root;
    }

    public static string GenerateText() => Generate().ToJsonString(ConfigurationJson.Options).ReplaceLineEndings("\n") + "\n";

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema, bool keepRequired)
    {
        if (context.TypeInfo.Type == typeof(TimeSpan))
        {
            schema = new JsonObject { ["type"] = "string", ["pattern"] = Duration.Pattern };
        }

        if (schema is not JsonObject node)
        {
            return schema;
        }

        if (!keepRequired)
        {
            node.Remove("required");
        }

        // A named entry can be removed with null, like any other value.
        var isMap = context.TypeInfo.Kind == JsonTypeInfoKind.Dictionary;
        if (isMap && node["additionalProperties"] is JsonObject entry && entry["type"] is JsonValue only)
        {
            entry["type"] = new JsonArray(only.GetValue<string>(), "null");
        }

        if (context.PropertyInfo is null)
        {
            AddFileOnlyKeys(context.TypeInfo.Type, node);
        }
        else if (context.PropertyInfo.AttributeProvider is PropertyInfo property && property.GetCustomAttribute<SettingAttribute>() is { } setting)
        {
            Describe(node, property, setting);
        }

        return node;
    }

    private static void Describe(JsonObject node, PropertyInfo property, SettingAttribute setting)
    {
        var description = setting.Description
            + (setting.Live ? " Live: the owner may change it during a run." : "")
            + (setting.Invariant is null ? "" : " It cannot be removed.");
        node.Insert(0, "description", description);
        node["examples"] = new JsonArray(JsonNode.Parse(setting.Example));
        if (property.DeclaringType!.GetConstructor(Type.EmptyTypes) is { } create && property.GetValue(create.Invoke(null)) is { } value)
        {
            node["default"] = JsonSerializer.SerializeToNode(value, property.PropertyType, ConfigurationJson.Options);
        }

        var types = Types(node);
        if (!double.IsNaN(setting.Minimum) && (types.Contains("number") || types.Contains("integer")))
        {
            node[setting.ExclusiveMinimum ? "exclusiveMinimum" : "minimum"] = setting.Minimum;
        }

        // In a layer, null removes a value so the code default applies (§13), except where that would weaken an invariant.
        if (setting.Invariant is not null && node["type"] is not null)
        {
            node["type"] = new JsonArray([.. types.Where(type => type != "null").Select(type => (JsonNode)type)]);
        }
        else if (node["type"] is not null && !types.Contains("null"))
        {
            node["type"] = new JsonArray([.. types.Append("null").Select(type => (JsonNode)type)]);
        }
        else if (node["type"] is null && node["enum"] is JsonArray choices)
        {
            choices.Add(null);
        }
    }

    private static string[] Types(JsonObject node) => node["type"] switch
    {
        JsonArray list => [.. list.Select(type => (string)type!)],
        JsonValue single => [(string)single!],
        _ => [],
    };

    /// <summary>Keys that exist only in files and are resolved while loading.</summary>
    private static void AddFileOnlyKeys(Type type, JsonObject node)
    {
        if (node["properties"] is not JsonObject properties)
        {
            return;
        }

        if (type == typeof(OfficinaOptions))
        {
            properties.Insert(0, "$schema", new JsonObject
            {
                ["description"] = "The JSON Schema of the file, for editor completion. Files only.",
                ["type"] = "string",
            });
            properties.Insert(1, "extends", new JsonObject
            {
                ["description"] = "Other files, relative to this one, that this file builds on. "
                    + "Each is a lower layer; later entries override earlier ones. Files only.",
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["examples"] = new JsonArray(new JsonArray("base.json")),
            });
        }
        else if (type == typeof(AgentDefinition))
        {
            properties.Insert(0, "extends", new JsonObject
            {
                ["description"] = "Another agent definition this one builds on. Its settings are the lower layer; cycles are rejected. Files only.",
                ["type"] = "string",
                ["examples"] = new JsonArray("base-coder"),
            });
        }
    }
}
