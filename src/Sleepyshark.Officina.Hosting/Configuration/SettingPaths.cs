namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>Setting paths such as <c>agents.developer.model</c> or <c>models.default.fallbacks[0]</c>.</summary>
internal static class SettingPaths
{
    public static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestor"/> or lies beneath it.</summary>
    public static bool IsWithin(string path, string ancestor) =>
        path == ancestor || ancestor.Length == 0 || (path.StartsWith(ancestor, StringComparison.Ordinal) && path[ancestor.Length] is '.' or '[');

    /// <summary>The enclosing path; the top level is the empty path.</summary>
    public static string Parent(string path) => path[..Math.Max(0, Math.Max(path.LastIndexOf('.'), path.LastIndexOf('[')))];
}
