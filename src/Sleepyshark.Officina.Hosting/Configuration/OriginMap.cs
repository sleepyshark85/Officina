namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Where each value of the merged configuration came from (CFG-04), and where each part of every layer was written,
/// for error positions.
/// </summary>
internal sealed class OriginMap
{
    private readonly Dictionary<string, ConfigurationOrigin> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConfigurationOrigin> written = new(StringComparer.Ordinal);

    /// <summary>The origin of every value a layer set, by path. A value that is not here is a code default.</summary>
    public IReadOnlyDictionary<string, ConfigurationOrigin> Values => values;

    public void Set(string path, ConfigurationOrigin origin) => values[path] = origin;

    /// <summary>Removes and returns the origins of the value at <paramref name="path"/> and everything beneath it.</summary>
    public Dictionary<string, ConfigurationOrigin> Take(string path)
    {
        var taken = values.Where(entry => SettingPaths.IsWithin(entry.Key, path)).ToDictionary(StringComparer.Ordinal);
        foreach (var key in taken.Keys)
        {
            values.Remove(key);
        }

        return taken;
    }

    /// <summary>Records where a part of a layer was written, whether or not it ends up in effect.</summary>
    public void Written(string path, ConfigurationOrigin origin) => written[path] = origin;

    /// <summary>Where a setting, or the nearest part of the configuration around it, was written.</summary>
    public ConfigurationOrigin? LocationOf(string path)
    {
        for (var current = path; ; current = SettingPaths.Parent(current))
        {
            if ((values.GetValueOrDefault(current) ?? written.GetValueOrDefault(current)) is { } origin)
            {
                return origin;
            }

            if (current.Length == 0)
            {
                return null;
            }
        }
    }
}
