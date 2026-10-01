using System.Reflection;
using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The paths of the settings the Options classes declare. It descends into every type that declares
/// <see cref="SettingAttribute"/> properties, wherever that type is defined, and into named entries.
/// </summary>
public static class SettingWalker
{
    /// <summary>
    /// The path of every setting, with <c>*</c> for the name of a named entry, such as <c>agents.*.model</c>.
    /// Configuration may set exactly these.
    /// </summary>
    public static IEnumerable<string> KnownPaths() => Paths(typeof(OfficinaOptions), "");

    private static IEnumerable<string> Paths(Type type, string path) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<SettingAttribute>() is not null)
            .SelectMany(property => TypePaths(property.PropertyType, Join(path, JsonNamingPolicy.CamelCase.ConvertName(property.Name))));

    private static IEnumerable<string> TypePaths(Type type, string path)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))
        {
            return TypePaths(type.GetGenericArguments()[1], Join(path, "*"));
        }

        return IsSection(type) ? Paths(type, path) : [path];
    }

    private static bool IsSection(Type type) => type.GetProperties().Any(property => property.GetCustomAttribute<SettingAttribute>() is not null);

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
