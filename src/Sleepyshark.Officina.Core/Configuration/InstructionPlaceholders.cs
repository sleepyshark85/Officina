using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Placeholders in an agent's instructions, written <c>{{namespace.name}}</c> (CFG-14). Instructions are the same
/// for every call (the stable prefix), so they may use only the project's and the agent's own values. A placeholder
/// that cannot be filled is an error, never an empty string.
/// </summary>
public static partial class InstructionPlaceholders
{
    private static readonly string[] Volatile = ["caller", "work", "now"];
    private static readonly string[] Secret = ["secret", "secrets", "env"];

    /// <summary>The problems of the placeholders in every agent's instructions.</summary>
    public static IEnumerable<ConfigurationError> Check(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Agents
            .Where(agent => agent.Value.Instructions is not null)
            .SelectMany(agent => Check(agent.Value.Instructions, $"agents.{agent.Key}.instructions", options.Project, agent.Key, agent.Value));
    }

    /// <summary>Fills the placeholders of instructions. Validation has already rejected any that cannot be filled.</summary>
    public static string Fill(string instructions, ProjectOptions project, string agentName, AgentDefinition agent) =>
        Expression().Replace(instructions, placeholder => Resolve(placeholder, project, agentName, agent)
            ?? throw new InvalidOperationException($"Placeholder {placeholder.Value} cannot be filled."));

    private static IEnumerable<ConfigurationError> Check(string text, string path, ProjectOptions project, string agentName, AgentDefinition agent)
    {
        foreach (Match placeholder in Expression().Matches(text))
        {
            var ns = placeholder.Groups["namespace"].Value;
            if (Secret.Contains(ns, StringComparer.OrdinalIgnoreCase))
            {
                // INV-06: a secret never reaches model input.
                yield return new(ValidationPhase.Invariants, path, $"placeholder {placeholder.Value} would put a secret into text the model reads.",
                    "Secrets are never placeholders; give the secret to the provider that needs it.");
            }
            else if (Volatile.Contains(ns))
            {
                // CTX-02: the stable prefix does not depend on the caller, the work or the time.
                yield return new(ValidationPhase.Prefix, path, $"placeholder {placeholder.Value} is not allowed in instructions.",
                    "Instructions are the same for every call, so they cannot use caller, work or time values.");
            }
            else if (Resolve(placeholder, project, agentName, agent) is null)
            {
                var problem = $"placeholder {placeholder.Value} cannot be filled; it is never replaced with an empty string.";
                yield return new(ValidationPhase.References, path, problem,
                    ns switch
                    {
                        "project" => "Set the value it names: project.name or project.values.<name>.",
                        "agent" => "Use agent.name, or give the agent a description for agent.description.",
                        _ => "Use the project or agent namespace in instructions.",
                    });
            }
        }
    }

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
