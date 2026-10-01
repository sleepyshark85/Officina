using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Hosting.Documentation;

/// <summary>
/// Generates the JSON Schema of configuration files from the Options classes (CFG-15). It is for editor
/// completion and early feedback; full validation is still CFG-06. Every file is a layer that may set
/// only some settings, so the schema requires nothing.
/// </summary>
public static class SchemaGenerator
{
    public const string SchemaId = "https://raw.githubusercontent.com/sleepyshark85/Officina/main/docs/officina.schema.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Generate(SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var definitions = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        var generator = new Generator(model, definitions);
        var root = generator.Section(model.Root);
        var schema = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = SchemaId,
            ["title"] = "Officina configuration",
            ["description"] = "Generated from the Options classes of Sleepyshark.Officina.Core. Do not edit; see docs/configuration-settings.md.",
        };

        foreach (var (key, value) in root)
        {
            schema[key] = value?.DeepClone();
        }

        schema["$defs"] = new JsonObject(definitions.Select(entry => KeyValuePair.Create(entry.Key, (JsonNode?)entry.Value)));
        return schema.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }

    private sealed class Generator(SettingsModel model, SortedDictionary<string, JsonNode> definitions)
    {
        public JsonObject Section(ObjectShape shape)
        {
            var defaults = shape.Type.IsAbstract ? null : shape.CreateDefault();
            var properties = new JsonObject();
            foreach (var key in shape.FileOnly)
            {
                properties[key.Name] = Describe(
                    key.Kind == FileOnlySettingKind.Text
                        ? new JsonObject { ["type"] = "string" }
                        : new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                    key.Description, key.Example);
            }

            foreach (var property in shape.Properties)
            {
                var schema = Describe(Value(property.Type, property), property.Info.Description, property.Info.Example);
                if (defaults is not null && property.GetValue(defaults) is { } value)
                {
                    schema["default"] = OptionsWriter.WriteValue(value, property.Type, model);
                }

                // null removes a value in a higher layer (§13), except where it would weaken an invariant.
                properties[property.Name] = property.Info.Invariant is null ? AllowNull(schema) : schema;
            }

            return new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        }

        private JsonObject Value(SettingType type, SettingProperty? property)
        {
            var info = property?.Info;
            switch (type.Kind)
            {
                case SettingKind.Section:
                    return Reference(type.Shape!);
                case SettingKind.Map:
                    return new JsonObject
                    {
                        ["type"] = "object",
                        ["propertyNames"] = new JsonObject { ["pattern"] = "^[A-Za-z0-9_][A-Za-z0-9_-]*$" },
                        ["additionalProperties"] = AllowNull(Value(type.Element!, null)),
                    };
                case SettingKind.List:
                    return new JsonObject { ["type"] = "array", ["items"] = Value(type.Element!, null) };
                case SettingKind.Text when info?.AllowFile == true:
                    return new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, Definition("fileInclude", FileInclude)) };
                case SettingKind.Text:
                    return new JsonObject { ["type"] = "string" };
                case SettingKind.WholeNumber:
                    return Range(new JsonObject { ["type"] = "integer" }, info);
                case SettingKind.Number:
                    return Range(new JsonObject { ["type"] = "number" }, info);
                case SettingKind.Boolean:
                    return new JsonObject { ["type"] = "boolean" };
                case SettingKind.Choice:
                    return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray([.. type.Choices.Select(choice => (JsonNode)choice)]) };
                case SettingKind.Duration:
                    return Definition("duration", () => new JsonObject
                    {
                        ["type"] = "string",
                        ["pattern"] = Durations.Pattern,
                        ["description"] = "A number with a unit: ms, s, m, h or d.",
                    });
                case SettingKind.Secret:
                    return Definition("secret", () => new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "A secret, by name. Its value is read from the secret source when it is used (CFG-09).",
                        ["properties"] = new JsonObject { ["secret"] = new JsonObject { ["type"] = "string", ["pattern"] = "^[A-Za-z_][A-Za-z0-9_]*$" } },
                        ["required"] = new JsonArray("secret"),
                        ["additionalProperties"] = false,
                    });
                case SettingKind.ModelReference:
                    return new JsonObject { ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "string", ["minLength"] = 1 }, Reference(type.Shape!)) };
                case SettingKind.Capabilities:
                    var capabilities = new JsonObject();
                    foreach (var capability in model.Capabilities.All)
                    {
                        capabilities[capability.Name] = AllowNull(new JsonObject
                        {
                            ["description"] = capability.Description,
                            ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "boolean" }, Reference(model.ShapeOf(capability.SettingsType))),
                        });
                    }

                    return new JsonObject { ["type"] = "object", ["properties"] = capabilities, ["additionalProperties"] = false };
                case SettingKind.Condition:
                    return Definition("condition", Condition);
                default:
                    return new JsonObject();
            }
        }

        private JsonObject Reference(ObjectShape shape)
        {
            var name = shape.Type.Name;
            if (!definitions.ContainsKey(name))
            {
                definitions[name] = new JsonObject();
                definitions[name] = Section(shape);
            }

            return new JsonObject { ["$ref"] = "#/$defs/" + name };
        }

        private JsonObject Definition(string name, Func<JsonObject> create)
        {
            if (!definitions.ContainsKey(name))
            {
                definitions[name] = new JsonObject();
                definitions[name] = create();
            }

            return new JsonObject { ["$ref"] = "#/$defs/" + name };
        }

        private static JsonObject FileInclude() => new()
        {
            ["type"] = "object",
            ["description"] = "The contents of a file, relative to the file that contains this value.",
            ["properties"] = new JsonObject { ["file"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } },
            ["required"] = new JsonArray("file"),
            ["additionalProperties"] = false,
        };

        private static JsonObject Condition()
        {
            var self = new JsonObject { ["$ref"] = "#/$defs/condition" };
            JsonObject Combinator(string key, JsonNode body) => new()
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { [key] = body },
                ["required"] = new JsonArray(key),
                ["additionalProperties"] = false,
            };

            var number = new JsonObject { ["type"] = "number" };
            return new JsonObject
            {
                ["description"] = "A condition in the condition language (CFG-13).",
                ["oneOf"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["field"] = new JsonObject { ["type"] = "string" },
                            ["equals"] = new JsonObject { ["type"] = new JsonArray("string", "number", "boolean", "null") },
                            ["in"] = new JsonObject { ["type"] = "array" },
                            ["gt"] = number.DeepClone(), ["gte"] = number.DeepClone(), ["lt"] = number.DeepClone(), ["lte"] = number.DeepClone(),
                            ["exists"] = new JsonObject { ["type"] = "boolean" },
                        },
                        ["required"] = new JsonArray("field"),
                        ["minProperties"] = 2,
                        ["maxProperties"] = 2,
                        ["additionalProperties"] = false,
                    },
                    Combinator("all", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = self.DeepClone() }),
                    Combinator("any", new JsonObject { ["type"] = "array", ["minItems"] = 1, ["items"] = self.DeepClone() }),
                    Combinator("not", self.DeepClone())),
            };
        }

        private static JsonObject Range(JsonObject schema, SettingAttribute? info)
        {
            if (info is null)
            {
                return schema;
            }

            if (!double.IsNaN(info.Minimum))
            {
                schema[info.ExclusiveMinimum ? "exclusiveMinimum" : "minimum"] = info.Minimum;
            }

            if (!double.IsNaN(info.Maximum))
            {
                schema["maximum"] = info.Maximum;
            }

            return schema;
        }

        private static JsonObject Describe(JsonObject schema, string description, string? example)
        {
            var described = new JsonObject { ["description"] = description };
            foreach (var (key, value) in schema)
            {
                described[key] = value?.DeepClone();
            }

            if (example is not null)
            {
                described["examples"] = new JsonArray(JsonNode.Parse(example));
            }

            return described;
        }

        private static JsonObject AllowNull(JsonObject schema)
        {
            var nullable = new JsonObject();
            var keywords = new JsonObject();
            foreach (var (key, value) in schema)
            {
                (key is "description" or "default" or "examples" ? nullable : keywords)[key] = value?.DeepClone();
            }

            nullable["anyOf"] = new JsonArray(keywords, new JsonObject { ["type"] = "null" });
            return nullable;
        }
    }
}
