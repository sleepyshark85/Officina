using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>The dependency rules of DESIGN.md §1, checked by TEST-32.</summary>
internal static partial class DependencyRules
{
    public const string ClaudeProvider = "Sleepyshark.Officina.Providers.Claude";
    public const string Core = "Sleepyshark.Officina.Core";

    /// <summary>The JSON Schema validator, the one package Core may use besides the base library (DESIGN.md §1).</summary>
    public const string JsonSchemaValidator = "JsonSchema.Net";

    /// <summary>Package id prefixes of AI and agent frameworks no project may use.</summary>
    /// <remarks>
    /// The official MCP SDK is listed because it brings in Microsoft.Extensions.AI; Officina has its own MCP client.
    /// </remarks>
    private static readonly string[] OtherAiFrameworks =
    [
        "Microsoft.Agents", "Microsoft.SemanticKernel", "Microsoft.AutoGen", "AutoGen",
        "OpenAI", "Azure.AI", "Betalgo.OpenAI", "Google.GenAI", "Google.Cloud.AIPlatform", "Mscc.GenerativeAI",
        "Mistral", "Cohere", "OllamaSharp", "LLamaSharp", "LangChain", "Claudia", "ModelContextProtocol",
    ];

    public static IEnumerable<string> Check(ProjectDependencies project)
    {
        var isProvider = Is(project.Name, ClaudeProvider);

        // The provider's own graph is walked up to the Anthropic SDK; everyone else's up to the provider.
        var reached = project.Reachable(id => isProvider ? IsAnthropicSdk(id) : Is(id, ClaudeProvider));

        foreach (var id in reached.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (IsAnthropicSdk(id) && !isProvider)
            {
                yield return $"{project.Name} references the Anthropic SDK ({id}). Only {ClaudeProvider} may.";
            }
            else if (HasPrefix(id, "Microsoft.Extensions.AI"))
            {
                yield return $"{project.Name} gets {id} other than through the Anthropic SDK in {ClaudeProvider}.";
            }
            else if (OtherAiFrameworks.Any(prefix => HasPrefix(id, prefix)))
            {
                yield return $"{project.Name} references the AI or agent framework {id}.";
            }
        }

        if (Is(project.Name, Core))
        {
            foreach (var id in project.Direct.Where(id => !Is(id, JsonSchemaValidator)).Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return $"{Core} must depend on the .NET base library and {JsonSchemaValidator} only, but references {id}.";
            }

            foreach (var framework in project.FrameworkReferences.Where(name => !Is(name, "Microsoft.NETCore.App")))
            {
                yield return $"{Core} must depend on the .NET base library and {JsonSchemaValidator} only, but references the {framework} framework.";
            }
        }
    }

    /// <summary>Finds code that uses Microsoft.Extensions.AI types, which no project may do.</summary>
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
