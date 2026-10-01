using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Merges layers by the rules of configuration reference §13 and keeps the origin of every value: objects and named
/// maps key by key, anything else replaced as a whole, and <c>null</c> restoring the code default. Settings that
/// protect an invariant cannot be removed (INV-07).
/// </summary>
internal sealed class LayerMerger
{
    private static readonly Dictionary<string, string> Protected = ProtectedSettings(typeof(OfficinaOptions), "").ToDictionary(StringComparer.Ordinal);

    private readonly JsonObject defaults;
    private readonly OriginMap origins;
    private readonly ConfigurationErrors errors;

    /// <param name="defaults">The code defaults, the lowest layer.</param>
    /// <param name="origins">Where the origin of each merged value is kept.</param>
    /// <param name="errors">Where attempts to remove a protected setting are reported.</param>
    public LayerMerger(JsonObject defaults, OriginMap origins, ConfigurationErrors errors)
    {
        this.defaults = defaults;
        this.origins = origins;
        this.errors = errors;
        Merged = (JsonObject)defaults.DeepClone();
    }

    /// <summary>The layers merged so far. A removed value without a default stays as a null marker.</summary>
    public JsonObject Merged { get; }

    public void Apply(Layer layer)
    {
        foreach (var (path, origin) in layer.Positions)
        {
            origins.Written(path, origin);
        }

        Merge(Merged, layer.Root, defaults, "", layer.OriginOf);
    }

    /// <summary>Merges <paramref name="upper"/> into <paramref name="target"/>; the origin of each value it sets comes from <paramref name="originOf"/>.</summary>
    public void Merge(JsonObject target, JsonObject upper, JsonNode? lowerDefaults, string path, Func<string, ConfigOrigin> originOf)
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

            if (value is null && Protected.TryGetValue(childPath, out var invariant))
            {
                errors.Add(ValidationPhase.Invariants, childPath, "cannot be removed with null.",
                    $"Set a limit; it can be high, but it always exists ({invariant}). Leave the setting out to use the default.", originOf(childPath));
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

    private static IEnumerable<KeyValuePair<string, string>> ProtectedSettings(Type type, string path) =>
        type.GetProperties().SelectMany(property =>
        {
            var childPath = SettingPaths.Join(path, JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            IEnumerable<KeyValuePair<string, string>> own = property.GetCustomAttribute<SettingAttribute>()?.Invariant is { } invariant ? [KeyValuePair.Create(childPath, invariant)] : [];
            return property.PropertyType.Namespace == type.Namespace && property.PropertyType.IsClass ? own.Concat(ProtectedSettings(property.PropertyType, childPath)) : own;
        });
}
