using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration.Placeholders;

/// <summary>
/// A placeholder in text, written <c>{{namespace.name}}</c> with an optional format, such as
/// <c>{{project.values.testCommand}}</c> or <c>{{now:date}}</c> (CFG-14).
/// </summary>
/// <param name="Text">The placeholder as written, braces included.</param>
/// <param name="Namespace">The first name, such as <c>project</c>.</param>
/// <param name="Name">The rest of the dotted name, such as <c>values.testCommand</c>; empty for <c>{{now}}</c>.</param>
/// <param name="Format">The format after a colon, or null.</param>
/// <param name="Start">Where the placeholder starts in the text.</param>
public sealed partial record Placeholder(string Text, string Namespace, string Name, string? Format, int Start)
{
    /// <summary>The dotted name without the format, such as <c>project.values.testCommand</c>.</summary>
    public string FullName => Name.Length == 0 ? Namespace : Namespace + "." + Name;

    /// <summary>
    /// Every placeholder in the text, in order. Text between <c>{{</c> and <c>}}</c> that is not a dotted
    /// name, such as a template example with spaces, is not a placeholder and stays as it is.
    /// </summary>
    public static IReadOnlyList<Placeholder> FindAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return [.. Expression().Matches(text).Select(match => new Placeholder(
            match.Value,
            match.Groups["namespace"].Value,
            match.Groups["name"].Success ? match.Groups["name"].Value : "",
            match.Groups["format"].Success ? match.Groups["format"].Value : null,
            match.Index))];
    }

    /// <summary>Replaces every placeholder with the value <paramref name="resolve"/> gives.</summary>
    public static string Replace(string text, Func<Placeholder, string> resolve)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(resolve);
        return Expression().Replace(text, match => resolve(FindAll(match.Value)[0]));
    }

    [GeneratedRegex(@"\{\{(?<namespace>[A-Za-z_][A-Za-z0-9_-]*)(\.(?<name>[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*))?(:(?<format>[A-Za-z0-9_-]+))?\}\}")]
    private static partial Regex Expression();
}
