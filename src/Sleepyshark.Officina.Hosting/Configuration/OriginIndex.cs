using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>Where each value of the merged configuration came from, by setting path.</summary>
internal sealed class OriginIndex
{
    private readonly Dictionary<string, ConfigOrigin> values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConfigOrigin> nodes = new(StringComparer.Ordinal);

    public OriginIndex(ConfigObject root)
    {
        Index(root, "");
    }

    /// <summary>The origin of a value: lists and single values have one; a setting no layer set is a code default.</summary>
    public ConfigOrigin OriginOf(string path) => values.GetValueOrDefault(path) ?? ConfigOrigin.CodeDefault;

    /// <summary>Where a setting, or the nearest section around it, was written; null when only code defaults are involved.</summary>
    public string? LocationOf(string path)
    {
        for (string? current = path; current is not null; current = SettingPath.Parent(current))
        {
            if (nodes.TryGetValue(current, out var origin) && origin.Location is { } location)
            {
                return location;
            }
        }

        return null;
    }

    private void Index(ConfigNode node, string path)
    {
        if (path.Length > 0)
        {
            nodes[path] = node.Origin;
        }

        switch (node)
        {
            case ConfigObject obj when obj.Properties.Count > 0:
                foreach (var (key, value) in obj.Properties)
                {
                    Index(value, SettingPath.Child(path, key));
                }

                break;
            case ConfigArray array:
                values[path] = node.Origin;
                for (var index = 0; index < array.Items.Count; index++)
                {
                    nodes[SettingPath.Item(path, index)] = array.Items[index].Origin;
                }

                break;
            default:
                values[path] = node.Origin;
                break;
        }
    }
}
