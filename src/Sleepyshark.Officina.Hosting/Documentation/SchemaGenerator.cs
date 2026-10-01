using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Documentation;

/// <summary>
/// Generates the JSON Schema of configuration files from the Options classes with the built-in exporter, adding
/// the descriptions, examples, defaults and ranges of <see cref="SettingAttribute"/> (CFG-15). Every file is a
/// layer that may set only some settings, or remove one with <c>null</c>, so the schema requires nothing.
/// </summary>
public static class SchemaGenerator
{
    public const string SchemaId = "https://raw.githubusercontent.com/sleepyshark85/Officina/main/docs/officina.schema.json";

    public static JsonObject Generate()
    {
        var exporter = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true, TransformSchemaNode = Transform };
        var schema = JsonSchemaExporter.GetJsonSchemaAsNode(OfficinaJson.Options, typeof(OfficinaOptions), exporter).AsObject();
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

    public static string GenerateText() => Generate().ToJsonString(OfficinaJson.Options).ReplaceLineEndings("\n") + "\n";

    private static JsonNode Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (context.TypeInfo.Type == typeof(TimeSpan))
        {
            schema = new JsonObject { ["type"] = "string", ["pattern"] = "^[0-9]+(\\.[0-9]+)?(ms|s|m|h|d)$" };
        }

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
            + (setting.Live ? " Live: the owner may change it during a run." : "")
            + (setting.Invariant is { } invariant ? $" Cannot be removed or unlimited ({invariant})." : "");
        node.Insert(0, "description", description);
        node["examples"] = new JsonArray(JsonNode.Parse(setting.Example));
        if (property.DeclaringType!.GetConstructor(Type.EmptyTypes) is { } create && property.GetValue(create.Invoke(null)) is { } value)
        {
            node["default"] = JsonSerializer.SerializeToNode(value, property.PropertyType, OfficinaJson.Options);
        }

        if (!double.IsNaN(setting.Minimum) && node["type"]?.ToJsonString().Contains("string", StringComparison.Ordinal) != true)
        {
            node[setting.ExclusiveMinimum ? "exclusiveMinimum" : "minimum"] = setting.Minimum;
        }

        // In a layer, null removes a value so the code default applies (§13), except where that weakens an invariant.
        if (setting.Invariant is null)
        {
            switch (node["type"])
            {
                case JsonValue type:
                    node["type"] = new JsonArray(type.GetValue<string>(), "null");
                    break;
                case JsonArray types when !types.Any(type => type?.GetValue<string>() == "null"):
                    types.Add("null");
                    break;
                case null when node["enum"] is JsonArray choices:
                    choices.Add(null);
                    break;
            }
        }
        else if (node["type"] is JsonArray types)
        {
            node["type"] = new JsonArray([.. types.Where(type => type?.GetValue<string>() != "null").Select(type => type!.DeepClone())]);
        }
    }

    /// <summary>Keys that exist only in files and are resolved while loading.</summary>
    private static void AddFileOnlyKeys(Type type, JsonObject node)
    {
        if (node["properties"] is not JsonObject properties)
        {
            return;
        }

        if (type == typeof(OfficinaOptions))
        {
            properties.Insert(0, "$schema", new JsonObject { ["description"] = "The JSON Schema of the file, for editor completion. Files only.", ["type"] = "string" });
            properties.Insert(1, "extends", new JsonObject
            {
                ["description"] = "Other files, relative to this one, that this file builds on. Each is a lower layer; later entries override earlier ones. Files only.",
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["examples"] = new JsonArray(new JsonArray("base.json")),
            });
        }
        else if (type == typeof(AgentDefinition))
        {
            properties.Insert(0, "extends", new JsonObject
            {
                ["description"] = "Another agent definition this one builds on (CFG-05). Its settings are the lower layer; cycles are rejected. Files only.",
                ["type"] = "string",
                ["examples"] = new JsonArray("base-coder"),
            });
        }
    }
}
