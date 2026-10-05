using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>The dependency rules of ARCHITECTURE.md §3 and CLAUDE.md, checked by TEST-05.</summary>
internal static partial class DependencyRules
{
    public const string Claude = "Sleepyshark.Officina.Claude";
    public const string Core = "Sleepyshark.Officina";

    public static IEnumerable<string> Check(ProjectDependencies project)
    {
        var isClaude = Is(project.Name, Claude);

        // The Claude package's graph is walked up to the Anthropic SDK, which brings Microsoft.Extensions.AI with it;
        // everyone else's up to the Claude package.
        var reached = project.Reachable(id => isClaude ? IsAnthropicSdk(id) : Is(id, Claude));

        foreach (var id in reached.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsAnthropicSdk(id) && !isClaude)
            {
                yield return $"{project.Name} references the Anthropic SDK ({id}). Only {Claude} may.";
            }
            else if (HasPrefix(id, "Microsoft.Extensions.AI"))
            {
                yield return $"{project.Name} gets {id} other than through the Anthropic SDK in {Claude}.";
            }
            else if (HasPrefix(id, "Microsoft.Agents"))
            {
                yield return $"{project.Name} references Microsoft Agent Framework ({id}).";
            }
        }

        if (Is(project.Name, Core))
        {
            foreach (var id in project.Direct.Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return $"{Core} must depend on the .NET base library only, but references {id}.";
            }

            foreach (var framework in project.FrameworkReferences.Where(name => !Is(name, "Microsoft.NETCore.App")))
            {
                yield return $"{Core} must depend on the .NET base library only, but references the {framework} framework.";
            }
        }
    }

    /// <summary>Finds code that uses Microsoft.Extensions.AI types, which no project may do, even where the SDK brings them in.</summary>
    public static IEnumerable<string> CheckSource(string path, string text)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            if (ExtensionsAiNamespace().IsMatch(lines[index]))
            {
                yield return $"{path}:{index + 1} uses Microsoft.Extensions.AI types.";
            }
        }
    }

    private static bool IsAnthropicSdk(string id) => HasPrefix(id, "Anthropic");

    private static bool HasPrefix(string id, string prefix) =>
        Is(id, prefix) || id.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);

    private static bool Is(string id, string expected) => string.Equals(id, expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\bMicrosoft\.Extensions\.AI\b")]
    private static partial Regex ExtensionsAiNamespace();
}
