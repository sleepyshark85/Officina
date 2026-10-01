namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Merges layers by the rules of configuration reference §13: objects and named maps key by key, lists
/// and other values replaced as a whole, and <c>null</c> removing a value so the code default applies.
/// Every value keeps the origin of the layer that set it.
/// </summary>
internal static class LayerMerger
{
    /// <param name="lower">The merged lower layers.</param>
    /// <param name="upper">The next layer, in normal form.</param>
    /// <param name="defaults">The code default at the same place, which <c>null</c> restores.</param>
    public static ConfigNode Merge(ConfigNode? lower, ConfigNode upper, ConfigNode? defaults)
    {
        if (upper is ConfigNull)
        {
            // Where there is no default, the null stays as a marker, so a definition that extends
            // another still removes the base's value (§13); binding skips it.
            return defaults ?? upper;
        }

        if (upper is not ConfigObject upperObject)
        {
            return upper;
        }

        var lowerObject = lower as ConfigObject;
        var properties = new List<KeyValuePair<string, ConfigNode>>(lowerObject?.Properties ?? []);
        foreach (var (key, value) in upperObject.Properties)
        {
            var index = properties.FindIndex(property => property.Key == key);
            var merged = Merge(index >= 0 ? properties[index].Value : null, value, (defaults as ConfigObject)?.Get(key));
            if (index >= 0)
            {
                properties[index] = KeyValuePair.Create(key, merged);
            }
            else
            {
                properties.Add(KeyValuePair.Create(key, merged));
            }
        }

        // An object is located where it was first written, which is where readers look for it.
        var origin = lowerObject is { Origin.Layer: not LayerKind.CodeDefault } ? lowerObject.Origin : upperObject.Origin;
        return new ConfigObject(origin, properties);
    }
}
