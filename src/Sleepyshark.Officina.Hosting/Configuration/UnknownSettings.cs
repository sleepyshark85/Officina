using Microsoft.Extensions.Configuration;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Compares the configuration keys with the known setting paths of the Options classes (phase 2), because the binder
/// ignores what does not fit: keys that are not settings, a value where a section of settings belongs (such as a
/// secret written as text), and a section or list where a single value belongs. Every such key is reported.
/// </summary>
internal static class UnknownSettings
{
    /// <summary>Keys that exist only in files: the editor schema, and <c>extends</c> between agent definitions.</summary>
    private static readonly string[] FileOnly = ["$schema", "agents.*.extends"];

    private static readonly string[][] Known = [.. SettingWalker.KnownPaths().Concat(FileOnly).Select(path => path.Split('.'))];

    public static IEnumerable<ConfigurationError> Check(IConfiguration configuration, Func<string, ConfigurationOrigin?> originOf)
    {
        var reported = new List<string>();
        foreach (var (key, value) in configuration.AsEnumerable().OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
        {
            var underReported = reported.Any(parent => key.StartsWith(parent + ":", StringComparison.OrdinalIgnoreCase));
            if (underReported || Problem(key.Split(':'), value) is not { } found)
            {
                continue;
            }

            reported.Add(found.Key);
            yield return new ConfigurationError(ValidationPhase.Shape, found.Key.Replace(':', '.'), found.Problem, found.Fix)
            {
                Location = originOf(found.Key)?.Location,
            };
        }
    }

    private static (string Key, string Problem, string Fix)? Problem(string[] names, string? value)
    {
        if (IsLeaf(names))
        {
            return null;
        }

        if (IsSection(names))
        {
            // A section that holds a value instead of settings; MEC stores an empty object as a null value.
            return value is null or "" ? null : (Key(names), "is a single value, but it is a section of settings.", SectionFix(names));
        }

        for (var length = names.Length - 1; length > 0; length--)
        {
            if (IsLeaf(names[..length]))
            {
                return (Key(names[..length]), "is a list or a section, but it is a single value.", "Write one value.");
            }
        }

        return (Key(names), $"\"{names[^1]}\" is not a setting here.", "Remove it, or check the spelling against docs/configuration-settings.md.");
    }

    private static string SectionFix(string[] names) =>
        Known.Any(path => path.Length == names.Length + 1 && Prefix(path, names) && path[^1] == "secret")
            ? "Write { \"secret\": \"NAME\" } and put the value in the secret source, such as an environment variable NAME."
            : "Write its settings as an object; see docs/configuration-settings.md.";

    private static bool IsLeaf(string[] names) => Known.Any(path => path.Length == names.Length && Prefix(path, names));

    private static bool IsSection(string[] names) => Known.Any(path => path.Length > names.Length && Prefix(path, names));

    private static bool Prefix(string[] path, string[] names) =>
        names.Select((name, index) => path[index] == "*" || string.Equals(path[index], name, StringComparison.OrdinalIgnoreCase)).All(match => match);

    private static string Key(string[] names) => string.Join(':', names);
}
