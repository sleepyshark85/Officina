using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Placeholders in instructions, written <c>{{namespace.name}}</c> (CFG-14). Instructions are part of the stable
/// prefix, so they may use only values of the project and the agent definition; caller, work and time values
/// belong in the volatile context. A placeholder that cannot be filled is an error, never an empty string.
/// </summary>
public static partial class Placeholder
{
    private static readonly string[] Volatile = ["caller", "work", "now"];
    private static readonly string[] Secret = ["secret", "secrets", "env"];

    /// <summary>The problems of the placeholders in <paramref name="text"/>, reported at <paramref name="path"/>.</summary>
    public static IEnumerable<ConfigurationError> Check(string text, string path, ProjectOptions project, string agentName, AgentDefinition agent)
    {
        foreach (Match placeholder in Expression().Matches(text))
        {
            var ns = placeholder.Groups["namespace"].Value;
            if (Secret.Contains(ns, StringComparer.OrdinalIgnoreCase))
            {
                yield return new(ValidationPhase.Invariants, path, $"placeholder {placeholder.Value} would put a secret into text the model reads.",
                    "Secrets are never placeholders; give the secret to the provider that needs it (INV-06).");
            }
            else if (Volatile.Contains(ns))
            {
                yield return new(ValidationPhase.Prefix, path, $"placeholder {placeholder.Value} is not allowed in the stable prefix.",
                    "Move it to context.operatingFacts (CTX-02, CFG-14).");
            }
            else if (Resolve(placeholder, project, agentName, agent) is null)
            {
                yield return new(ValidationPhase.References, path, $"placeholder {placeholder.Value} cannot be filled; a placeholder is never filled with an empty string.",
                    ns switch
                    {
                        "project" => "Set the project value it names: project.name or project.values.<name>.",
                        "agent" => "Use agent.name, or give the agent a description for agent.description.",
                        _ => "Use the project or agent namespace in instructions.",
                    });
            }
        }
    }

    /// <summary>Fills the placeholders of instructions. Validation has already rejected any that cannot be filled.</summary>
    public static string Fill(string text, ProjectOptions project, string agentName, AgentDefinition agent) =>
        Expression().Replace(text, placeholder => Resolve(placeholder, project, agentName, agent)
            ?? throw new InvalidOperationException($"Placeholder {placeholder.Value} cannot be filled."));

    private static string? Resolve(Match placeholder, ProjectOptions project, string agentName, AgentDefinition agent) =>
        (placeholder.Groups["namespace"].Value, placeholder.Groups["name"].Value.Split('.')) switch
        {
            ("project", ["name"]) => project.Name,
            ("project", ["values", var key]) => project.Values.GetValueOrDefault(key),
            ("agent", ["name"]) => agentName,
            ("agent", ["description"]) => agent.Description,
            _ => null,
        };

    // Text between {{ and }} that is not a dotted name, such as a template example, is left as it is.
    [GeneratedRegex(@"\{\{(?<namespace>[A-Za-z_][A-Za-z0-9_-]*)(\.(?<name>[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*))?(:[A-Za-z0-9_-]+)?\}\}")]
    private static partial Regex Expression();
}
