using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>A JSON value from one layer, with where each part was written. Layers are merged as these trees.</summary>
internal abstract class ConfigNode(ConfigOrigin origin)
{
    public ConfigOrigin Origin { get; } = origin;

    public static ConfigNode FromJson(JsonNode? json, ConfigOrigin origin) => json switch
    {
        null => new ConfigNull(origin),
        JsonObject obj => new ConfigObject(origin, obj.Select(property => KeyValuePair.Create(property.Key, FromJson(property.Value, origin)))),
        JsonArray array => new ConfigArray(origin, array.Select(item => FromJson(item, origin))),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => ConfigScalar.Text(value.GetValue<string>(), origin),
            JsonValueKind.Number => new ConfigScalar(origin, JsonValueKind.Number, value.ToJsonString()),
            JsonValueKind.True => new ConfigScalar(origin, JsonValueKind.True, "true"),
            JsonValueKind.False => new ConfigScalar(origin, JsonValueKind.False, "false"),
            _ => new ConfigNull(origin),
        },
        _ => throw new InvalidOperationException("Unknown JSON node."),
    };

    public abstract JsonNode? ToJson();

    public JsonElement ToElement() => JsonSerializer.SerializeToElement(ToJson());
}

internal sealed class ConfigObject(ConfigOrigin origin, IEnumerable<KeyValuePair<string, ConfigNode>> properties) : ConfigNode(origin)
{
    public IReadOnlyList<KeyValuePair<string, ConfigNode>> Properties { get; } = [.. properties];

    public ConfigNode? Get(string key) => Properties.FirstOrDefault(property => property.Key == key).Value;

    public ConfigObject Without(string key) => new(Origin, Properties.Where(property => property.Key != key));

    public override JsonNode ToJson() => new JsonObject(Properties.Select(property => KeyValuePair.Create(property.Key, property.Value.ToJson())));
}

internal sealed class ConfigArray(ConfigOrigin origin, IEnumerable<ConfigNode> items) : ConfigNode(origin)
{
    public IReadOnlyList<ConfigNode> Items { get; } = [.. items];

    public override JsonNode ToJson() => new JsonArray([.. Items.Select(item => item.ToJson())]);
}

/// <summary>A string, number or boolean. <see cref="Raw"/> is the string's value, or the number's or boolean's JSON text.</summary>
internal sealed class ConfigScalar(ConfigOrigin origin, JsonValueKind kind, string raw, bool lenient = false) : ConfigNode(origin)
{
    public JsonValueKind Kind { get; } = kind;

    public string Raw { get; } = raw;

    /// <summary>Whether the value came from text that was not written as JSON (an environment variable or run option), so it may be read as the type the setting expects.</summary>
    public bool Lenient { get; } = lenient;

    public static ConfigScalar Text(string value, ConfigOrigin origin, bool lenient = false) => new(origin, JsonValueKind.String, value, lenient);

    public override JsonNode? ToJson() => Kind switch
    {
        JsonValueKind.String => JsonValue.Create(Raw),
        JsonValueKind.Number => JsonNode.Parse(Raw),
        JsonValueKind.True => JsonValue.Create(true),
        _ => JsonValue.Create(false),
    };

    public string Describe() => Kind switch
    {
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        _ => "true or false",
    };

    public bool TryGetDecimal(out decimal value) =>
        decimal.TryParse(Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

internal sealed class ConfigNull(ConfigOrigin origin) : ConfigNode(origin)
{
    public override JsonNode? ToJson() => null;
}
