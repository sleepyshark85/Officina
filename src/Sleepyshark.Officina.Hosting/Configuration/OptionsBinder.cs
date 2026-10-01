using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Binds the merged configuration to the Options classes. The shape was checked against the schema first, so binding
/// succeeds; if the schema and the classes ever disagree, that is reported as an error rather than a crash.
/// </summary>
internal static class OptionsBinder
{
    public static OfficinaOptions Bind(JsonObject merged, LoadErrors errors)
    {
        try
        {
            return merged.Deserialize<OfficinaOptions>(ConfigurationJson.Options)!;
        }
        catch (JsonException exception)
        {
            var path = (exception.Path ?? "$").TrimStart('$', '.');
            errors.Add(ValidationPhase.Shape, path, "cannot be read as the setting it is.",
                "Check its value against docs/configuration-settings.md.", null);
            return new OfficinaOptions();
        }
    }
}
