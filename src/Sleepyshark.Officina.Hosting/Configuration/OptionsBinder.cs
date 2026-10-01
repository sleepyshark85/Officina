using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Binds the merged layers to the Options classes with System.Text.Json, strictly: unknown settings, wrong types and
/// missing required settings are errors (phase 2). The serializer stops at the first error, so each bad value is
/// reported with its path and position, removed, and binding retried until it succeeds.
/// </summary>
internal static partial class OptionsBinder
{
    private const int MaxErrors = 100;

    public static OfficinaOptions Bind(JsonObject merged, OriginMap origins, ConfigurationErrors errors)
    {
        for (var attempt = 0; attempt < MaxErrors; attempt++)
        {
            try
            {
                return merged.Deserialize<OfficinaOptions>(OfficinaJson.Options)!;
            }
            catch (JsonException exception)
            {
                var names = Names(exception.Path ?? "$");
                var (path, problem, fix) = Describe(exception.Message, string.Join('.', names));
                if (!errors.IsRejected(path))
                {
                    errors.Add(ValidationPhase.Shape, path, problem, fix, origins.LocationOf(path));
                }

                if (names.Length == 0 || !Remove(merged, names))
                {
                    break;
                }
            }
        }

        return new OfficinaOptions();
    }

    /// <summary>The path, problem and fix of a serializer error, in the words of configuration rather than of .NET types.</summary>
    private static (string Path, string Problem, string Fix) Describe(string message, string path)
    {
        if (Unmapped().Match(message) is { Success: true } unmapped)
        {
            return (path, $"\"{unmapped.Groups["name"].Value}\" is not a setting here.", "Remove it, or check the spelling against docs/configuration-settings.md.");
        }

        if (Required().Match(message) is { Success: true } required)
        {
            var name = required.Groups["name"].Value;
            return (SettingPaths.Join(path, name), "is required but not set.", $"Add \"{name}\" to {path}. For an agent, that is its job: only the application knows it (CFG-17).");
        }

        var type = Converted().Match(message).Groups["type"].Value;
        if (type.EndsWith(nameof(SecretReference), StringComparison.Ordinal))
        {
            // The value is not repeated: it may be the secret itself.
            return (path, "is not a secret reference; a secret is never written in configuration.",
                "Write { \"secret\": \"NAME\" } and put the value in the secret source, such as an environment variable NAME (CFG-09).");
        }

        var expected = type switch
        {
            _ when type.Contains("Int32", StringComparison.Ordinal) => "a whole number",
            _ when type.Contains("Decimal", StringComparison.Ordinal) => "a number",
            "System.String" => "text",
            "a duration" => "a duration: a number with a unit, ms, s, m, h or d, such as \"30m\"",
            _ when type.Contains("IReadOnlyList", StringComparison.Ordinal) => "a list",
            _ when typeof(OfficinaOptions).Assembly.GetType(type) is { IsEnum: true } choice =>
                "one of " + string.Join(", ", Enum.GetNames(choice).Select(name => $"\"{JsonNamingPolicy.CamelCase.ConvertName(name)}\"")),
            _ => "an object of settings",
        };
        return (path, $"is not {expected}.", $"Write {expected}.");
    }

    /// <summary>The names in a serializer path such as <c>$.agents['my agent'].instructions</c>.</summary>
    private static string[] Names(string path) =>
        [.. PathName().Matches(path).Select(match => match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["quoted"].Value.Replace("''", "'", StringComparison.Ordinal))];

    private static bool Remove(JsonObject root, string[] names)
    {
        var parent = names[..^1].Aggregate((JsonNode?)root, (node, name) => (node as JsonObject)?[name]) as JsonObject;
        return parent?.Remove(names[^1]) == true;
    }

    [GeneratedRegex(@"\.(?<name>[^.\[]+)|\['(?<quoted>(?:[^']|'')*)'\]")]
    private static partial Regex PathName();

    [GeneratedRegex("The JSON property '(?<name>.*)' could not be mapped")]
    private static partial Regex Unmapped();

    [GeneratedRegex("missing required properties including: '(?<name>[^']*)'")]
    private static partial Regex Required();

    [GeneratedRegex(@"could not be converted to (?<type>.+?)\.( Path:.*)?$")]
    private static partial Regex Converted();
}
