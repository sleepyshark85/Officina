using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The JSON form of the Options classes: the configuration file format (configuration reference §2), also used to
/// store the resolved configuration of each run (CFG-07).
/// </summary>
public static class ConfigurationJson
{
    /// <summary>Strict serializer settings: unknown settings, wrong types and missing required settings are errors.</summary>
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>The configuration as indented JSON with every setting, defaults included.</summary>
    public static string Write(OfficinaOptions options) => JsonSerializer.Serialize(options, Options).ReplaceLineEndings("\n");

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
