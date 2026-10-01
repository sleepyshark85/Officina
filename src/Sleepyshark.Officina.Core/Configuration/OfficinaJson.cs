using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// How the Options classes are written as JSON: the file format of configuration (configuration reference §2),
/// also used to store the resolved configuration of each run (CFG-07).
/// </summary>
public static partial class OfficinaJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>The configuration as indented JSON with every setting, defaults included.</summary>
    public static string Write(OfficinaOptions options) =>
        JsonSerializer.Serialize(options, Options).ReplaceLineEndings("\n");

    /// <summary>Writes a duration in the largest unit that represents it exactly, such as <c>"8h"</c>.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "0s";
        }

        foreach (var (unit, size) in Units)
        {
            if (duration.Ticks % size.Ticks == 0)
            {
                return (duration.Ticks / size.Ticks).ToString(CultureInfo.InvariantCulture) + unit;
            }
        }

        return duration.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    public static bool TryParseDuration(string text, out TimeSpan duration)
    {
        duration = default;
        var match = Duration().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var number = decimal.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        duration = TimeSpan.FromTicks((long)(number * Units.First(unit => unit.Unit == match.Groups["unit"].Value).Size.Ticks));
        return true;
    }

    private static readonly (string Unit, TimeSpan Size)[] Units =
    [
        ("d", TimeSpan.FromDays(1)), ("h", TimeSpan.FromHours(1)), ("m", TimeSpan.FromMinutes(1)),
        ("s", TimeSpan.FromSeconds(1)), ("ms", TimeSpan.FromMilliseconds(1)),
    ];

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false), new DurationConverter() },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    [GeneratedRegex("^(?<number>[0-9]+(\\.[0-9]+)?)(?<unit>ms|s|m|h|d)$")]
    private static partial Regex Duration();

    private sealed class DurationConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && TryParseDuration(reader.GetString()!, out var duration)
                ? duration
                : throw new JsonException("The JSON value could not be converted to a duration.");

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options) =>
            writer.WriteStringValue(FormatDuration(value));
    }
}
