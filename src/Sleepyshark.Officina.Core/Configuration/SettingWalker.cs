using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Visits every setting of bound Options with its path. It descends into every object whose type declares
/// <see cref="SettingAttribute"/> properties, wherever that type is defined, and into named entries and lists.
/// </summary>
internal static class SettingWalker
{
    public static IEnumerable<SettingVisit> Walk(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Settings(options, "");
    }

    /// <summary>Whether values of the type are a section of settings.</summary>
    public static bool IsSection(Type type) => type.GetProperties().Any(property => property.GetCustomAttribute<SettingAttribute>() is not null);

    private static IEnumerable<SettingVisit> Settings(object owner, string path) =>
        owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => (Property: property, Setting: property.GetCustomAttribute<SettingAttribute>()))
            .Where(entry => entry.Setting is not null)
            .SelectMany(entry =>
            {
                var childPath = Join(path, JsonNamingPolicy.CamelCase.ConvertName(entry.Property.Name));
                var value = entry.Property.GetValue(owner);
                return Children(value, childPath).Prepend(new SettingVisit(childPath, entry.Setting, null, value));
            });

    private static IEnumerable<SettingVisit> Children(object? value, string path) => value switch
    {
        null or string => [],
        IDictionary map => Entries(map).SelectMany(entry =>
        {
            var name = (string)entry.Key;
            var entryPath = Join(path, name);
            return Children(entry.Value, entryPath).Prepend(new SettingVisit(entryPath, null, name, entry.Value));
        }),
        IEnumerable items => items.Cast<object?>().SelectMany((item, index) =>
            Children(item, $"{path}[{index}]").Prepend(new SettingVisit($"{path}[{index}]", null, null, item))),
        _ when IsSection(value.GetType()) => Settings(value, path),
        _ => [],
    };

    private static IEnumerable<DictionaryEntry> Entries(IDictionary map)
    {
        var entries = map.GetEnumerator();
        while (entries.MoveNext())
        {
            yield return entries.Entry;
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
