using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Any JSON value, for settings whose shape a provider or extension declares (MDL-02). Compares by content.</summary>
public sealed class SettingValue : IEquatable<SettingValue>
{
    private SettingValue(JsonElement element)
    {
        Element = element;
    }

    public JsonElement Element { get; }

    public static SettingValue Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new SettingValue(document.RootElement.Clone());
    }

    public static SettingValue From(JsonElement element) => new(element.Clone());

    public static implicit operator SettingValue(string value) => From(JsonSerializer.SerializeToElement(value));

    public static implicit operator SettingValue(double value) => From(JsonSerializer.SerializeToElement(value));

    public static implicit operator SettingValue(bool value) => From(JsonSerializer.SerializeToElement(value));

    public bool Equals(SettingValue? other) => other is not null && JsonElement.DeepEquals(Element, other.Element);

    public override bool Equals(object? obj) => Equals(obj as SettingValue);

    // Equal values can differ in raw text (1 and 1.0), so only the kind is hashed.
    public override int GetHashCode() => Element.ValueKind.GetHashCode();

    /// <summary>The value as compact JSON.</summary>
    public override string ToString() => JsonSerializer.Serialize(Element);
}
