using System.Reflection;

namespace Sleepyshark.Officina.Core;

/// <summary>The version of this core, as reported for code defaults (CFG-04) and stored with each run (CFG-07).</summary>
public static class CoreVersion
{
    public static string Value { get; } = Read();

    private static string Read()
    {
        var version = typeof(CoreVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CoreVersion).Assembly.GetName().Version?.ToString(3)
            ?? "unknown";

        // The build appends "+<commit>"; the release number is what readers need.
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
