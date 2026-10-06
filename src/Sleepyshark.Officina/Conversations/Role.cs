using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>Who a message is from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Role>))]
public enum Role
{
    [JsonStringEnumMemberName("user")]
    User,

    [JsonStringEnumMemberName("assistant")]
    Assistant,

    /// <summary>The host, with operator authority, after the cached prefix: the run context.</summary>
    [JsonStringEnumMemberName("operator")]
    Operator,
}
