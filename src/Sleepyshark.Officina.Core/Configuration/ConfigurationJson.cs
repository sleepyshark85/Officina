using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The JSON form of the Options classes: names and value forms as in configuration files. <c>sof config show</c> writes
/// settings with it, durable storage keeps each run's configuration in it (CFG-07), and the editor schema is generated
/// from it, which is why it disallows unknown members.
/// </summary>
public static class ConfigurationJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
