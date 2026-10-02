namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The presets shipped with the core (CFG-11): configuration files that a file names in its <c>extends</c> as
/// <c>preset:&lt;id&gt;</c>, a layer below it, which it overrides like any lower layer.
/// </summary>
public static class Presets
{
    /// <summary>The presets' ids.</summary>
    public static IReadOnlyList<string> Ids { get; } = ["coding-team", "single-call-extractor", "tool-using-assistant"];

    /// <summary>A preset's JSON text; null when there is no such preset.</summary>
    public static string? Read(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Ids.Contains(id, StringComparer.Ordinal))
        {
            return null;
        }

        using var stream = typeof(Presets).Assembly.GetManifestResourceStream($"Sleepyshark.Officina.Core.Presets.{id}.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
