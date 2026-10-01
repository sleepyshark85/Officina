using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Reads and writes a <see cref="TimeSpan"/> setting in the form of <see cref="Duration"/>.</summary>
internal sealed class DurationJsonConverter : JsonConverter<TimeSpan>
{
    public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Duration.TryParse(reader.GetString()!, out var duration)
            ? duration
            : throw new JsonException("The value is not a duration.");

    public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Duration.Format(value));
}
