using System.Globalization;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration.Model;

/// <summary>
/// Builds the paths that name settings in messages and in <c>sof config show</c>, such as
/// <c>agents.developer.model</c> or <c>models.default.fallbacks[0]</c>. A name that is not a plain
/// identifier is quoted: <c>agents["my agent"]</c>.
/// </summary>
public static partial class SettingPath
{
    public static string Child(string parent, string name)
    {
        if (!PlainName().IsMatch(name))
        {
            return $"{parent}[\"{name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"]";
        }

        return parent.Length == 0 ? name : parent + "." + name;
    }

    public static string Item(string parent, int index) => parent + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>The path of the enclosing setting, or null for a top-level one.</summary>
    public static string? Parent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int cut;
        if (path.EndsWith(']'))
        {
            cut = path.EndsWith("\"]", StringComparison.Ordinal) ? path.LastIndexOf("[\"", StringComparison.Ordinal) : path.LastIndexOf('[');
        }
        else
        {
            cut = path.LastIndexOf('.');
        }

        return cut <= 0 ? null : path[..cut];
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestor"/> or lies beneath it.</summary>
    public static bool IsWithin(string path, string ancestor) =>
        path == ancestor
        || ancestor.Length == 0
        || (path.StartsWith(ancestor, StringComparison.Ordinal) && path.Length > ancestor.Length && path[ancestor.Length] is '.' or '[');

    [GeneratedRegex("^[A-Za-z_$][A-Za-z0-9_-]*$")]
    private static partial Regex PlainName();
}
