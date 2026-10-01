using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>One value reached by <see cref="SettingWalker"/>.</summary>
/// <param name="Path">The setting's path, such as <c>models.default.fallbacks[0]</c>.</param>
/// <param name="Setting">The declaration of a property, or null for a map entry or list item.</param>
/// <param name="EntryName">The name of a map entry, or null.</param>
/// <param name="Value">The value, or null when unset.</param>
internal sealed record SettingVisit(string Path, SettingAttribute? Setting, string? EntryName, object? Value);

/// <summary>Visits every value of bound Options with its path, for the rules that apply to settings of any section.</summary>
internal static class SettingWalker
{
    public static IEnumerable<SettingVisit> Walk(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Section(options, "");
    }

    private static IEnumerable<SettingVisit> Section(object owner, string path) =>
        owner.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<SettingAttribute>() is not null)
            .SelectMany(property =>
            {
                var childPath = Join(path, JsonNamingPolicy.CamelCase.ConvertName(property.Name));
                var value = property.GetValue(owner);
                return Children(value, childPath).Prepend(new SettingVisit(childPath, property.GetCustomAttribute<SettingAttribute>(), null, value));
            });

    private static IEnumerable<SettingVisit> Children(object? value, string path) => value switch
    {
        IDictionary map => Entries(map).SelectMany(entry =>
        {
            var entryPath = Join(path, (string)entry.Key);
            return Children(entry.Value, entryPath).Prepend(new SettingVisit(entryPath, null, (string)entry.Key, entry.Value));
        }),
        IEnumerable<string> items => items.Select((item, index) => new SettingVisit($"{path}[{index}]", null, null, item)),
        not null when value.GetType().Namespace == typeof(OfficinaOptions).Namespace && value.GetType().IsClass => Section(value, path),
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
