using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Merges layers by the rules of configuration reference §13 and keeps the origin of every value: objects and named
/// maps key by key, anything else replaced as a whole, and <c>null</c> restoring the code default.
/// </summary>
/// <param name="defaults">The code defaults, the lowest layer.</param>
/// <param name="origins">Where the origin of each merged value is kept.</param>
internal sealed class LayerMerger(JsonObject defaults, OriginMap origins)
{
    /// <summary>The layers merged so far. A removed value without a default stays as a null marker.</summary>
    public JsonObject Merged { get; } = (JsonObject)defaults.DeepClone();

    public void Apply(Layer layer)
    {
        foreach (var (path, origin) in layer.Positions)
        {
            origins.Written(path, origin);
        }

        Merge(Merged, layer.Root, defaults, "", layer.OriginOf);
    }

    /// <summary>
    /// Merges <paramref name="upper"/> into <paramref name="target"/>; the origin of each value it sets comes from
    /// <paramref name="originOf"/>.
    /// </summary>
    public void Merge(JsonObject target, JsonObject upper, JsonNode? lowerDefaults, string path, Func<string, ConfigurationOrigin> originOf)
    {
        foreach (var (key, value) in upper.ToArray())
        {
            var childPath = SettingPaths.Join(path, key);
            var childDefaults = (lowerDefaults as JsonObject)?[key];
            if (value is JsonObject upperObject)
            {
                if (target[key] is not JsonObject)
                {
                    origins.Take(childPath);
                    target[key] = new JsonObject();
                }

                Merge(target[key]!.AsObject(), upperObject, childDefaults, childPath, originOf);
                continue;
            }

            origins.Take(childPath);

            // Without a default the null stays, so a definition that extends another still removes the base's value.
            target[key] = value is null ? childDefaults?.DeepClone() : value.DeepClone();
            if (value is not null)
            {
                origins.Set(childPath, originOf(childPath));
            }
        }
    }

    /// <summary>Removes the null markers once nothing more is merged.</summary>
    public static void RemoveNulls(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return;
        }

        foreach (var (key, value) in obj.ToArray())
        {
            if (value is null)
            {
                obj.Remove(key);
            }
            else
            {
                RemoveNulls(value);
            }
        }
    }
}
