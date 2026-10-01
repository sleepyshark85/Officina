using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Conditions;

namespace Sleepyshark.Officina.Core.Configuration.Model;

/// <summary>
/// Writes Options as JSON in the file format: every setting, in declaration order, with named items
/// sorted by name. The same Options always give the same text, whatever order the files were written in.
/// </summary>
public static class OptionsWriter
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Write(OfficinaOptions options, SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        return WriteObject(options, model.Root, model);
    }

    /// <summary>The canonical text of the configuration, as stored with each run (CFG-07).</summary>
    public static string WriteText(OfficinaOptions options, SettingsModel model) =>
        Write(options, model).ToJsonString(Indented).ReplaceLineEndings("\n");

    public static JsonObject WriteObject(object value, ObjectShape shape, SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(shape);
        var json = new JsonObject();

        // A capability that is off contributes no settings (CAP-02), so only its switch is written.
        if (value is CapabilitySettings { Enabled: false })
        {
            json["enabled"] = false;
            return json;
        }

        foreach (var property in shape.Properties)
        {
            if (property.GetValue(value) is { } setting)
            {
                json[property.Name] = WriteValue(setting, property.Type, model);
            }
        }

        return json;
    }

    public static JsonNode WriteValue(object value, SettingType type, SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(model);
        return type.Kind switch
        {
            SettingKind.Section => WriteObject(value, type.Shape!, model),
            SettingKind.Map => WriteMap((System.Collections.IEnumerable)value, entry => WriteValue(entry, type.Element!, model)),
            SettingKind.List => new JsonArray([.. ((System.Collections.IEnumerable)value).Cast<object>().Select(item => WriteValue(item, type.Element!, model))]),
            SettingKind.Text => JsonValue.Create((string)value),
            SettingKind.WholeNumber => JsonValue.Create(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)),
            SettingKind.Number => value is decimal number ? JsonValue.Create(number) : JsonValue.Create((double)value),
            SettingKind.Boolean => JsonValue.Create((bool)value),
            SettingKind.Choice => JsonValue.Create(type.ChoiceName(value)),
            SettingKind.Duration => JsonValue.Create(Durations.Format((TimeSpan)value)),
            SettingKind.Secret => new JsonObject { ["secret"] = ((SecretReference)value).Name },
            SettingKind.ModelReference => value is ModelReference { Name: { } name }
                ? JsonValue.Create(name)
                : WriteObject(((ModelReference)value).Profile!, type.Shape!, model),
            SettingKind.Capabilities => WriteMap((System.Collections.IEnumerable)value, entry => WriteObject(entry, model.ShapeOf(entry.GetType()), model)),
            SettingKind.Any => JsonNode.Parse(((SettingValue)value).Element.GetRawText())!,
            SettingKind.Condition => ((Condition)value).ToJson(),
            _ => throw new InvalidOperationException($"Cannot write a setting of kind {type.Kind}."),
        };
    }

    private static JsonObject WriteMap(System.Collections.IEnumerable map, Func<object, JsonNode> write)
    {
        var json = new JsonObject();
        foreach (var entry in map)
        {
            var (name, value) = Entry(entry);
            json[name] = write(value);
        }

        return json;
    }

    private static (string Name, object Value) Entry(object pair)
    {
        var type = pair.GetType();
        return ((string)type.GetProperty("Key")!.GetValue(pair)!, type.GetProperty("Value")!.GetValue(pair)!);
    }
}
