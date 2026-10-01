using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Documentation;

/// <summary>
/// Generates the JSON Schema of configuration files from the Options classes with the built-in exporter, adding the
/// descriptions, examples, defaults and ranges of <see cref="SettingAttribute"/> (CFG-15). The schema is for editors;
/// validation is done by the loader. A file may set only some settings, so the schema requires nothing.
/// </summary>
public static class SchemaGenerator
{
    public const string SchemaId = "https://raw.githubusercontent.com/sleepyshark85/Officina/main/docs/officina.schema.json";

    public static JsonObject Generate()
    {
        var exporter = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true, TransformSchemaNode = Transform };
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

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject node)
        {
            return schema;
        }

        node.Remove("required");
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
            + (property.GetCustomAttribute<RequiredAttribute>() is null ? "" : " Required.")
            + (setting.Live ? " Live: the owner may change it during a run." : "");
        node.Insert(0, "description", description);
        node["examples"] = new JsonArray(JsonNode.Parse(setting.Example));
        if (property.DeclaringType!.GetConstructor(Type.EmptyTypes) is { } create && property.GetValue(create.Invoke(null)) is { } value)
        {
            node["default"] = JsonSerializer.SerializeToNode(value, property.PropertyType, ConfigurationJson.Options);
        }

        var types = node["type"] switch
        {
            JsonArray list => list.Select(type => (string)type!).ToArray(),
            JsonValue single => [(string)single!],
            _ => [],
        };
        if (property.GetCustomAttribute<RangeAttribute>() is { } range && (types.Contains("number") || types.Contains("integer")))
        {
            var minimum = Convert.ToDouble(range.Minimum, System.Globalization.CultureInfo.InvariantCulture);
            node[range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum"] = minimum;
        }
    }

    /// <summary>Keys that exist only in files.</summary>
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
        }
        else if (type == typeof(AgentDefinition))
        {
            properties.Insert(0, "extends", new JsonObject
            {
                ["description"] = "Another agent definition this one builds on: it inherits every setting it does not set itself. Files only.",
                ["type"] = "string",
                ["examples"] = new JsonArray("base-coder"),
            });
        }
    }
}
