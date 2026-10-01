using System.Globalization;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Placeholders in an agent's instructions and operating facts, written <c>{{namespace.name}}</c> (CFG-14).
/// Instructions are the same for every call (the stable prefix), so they may use only the project's and the agent's own
/// values. Operating facts are rebuilt for every call, so they may also use the time, the caller and the work (CTX-09).
/// A placeholder that cannot be filled is an error, never an empty string.
/// </summary>
public static partial class InstructionPlaceholders
{
    private static readonly string[] Volatile = ["caller", "work", "now"];
    private static readonly string[] Secret = ["secret", "secrets", "env"];

    /// <summary>The problems of the placeholders in every agent's instructions and operating facts.</summary>
    public static IEnumerable<ConfigurationError> Check(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Agents.SelectMany(agent =>
            (agent.Value.Instructions is null ? [] : Check(agent.Value.Instructions, $"agents.{agent.Key}.instructions", options.Project, agent.Key, agent.Value, null))
            .Concat((agent.Value.Context?.OperatingFacts ?? []).SelectMany((fact, index) =>
                Check(fact, $"agents.{agent.Key}.context.operatingFacts[{index}]", options.Project, agent.Key, agent.Value, DateTimeOffset.UnixEpoch, Caller.Anonymous, ""))));
    }

    /// <summary>
    /// Fills the placeholders of instructions, or of an operating fact with the time <paramref name="now"/>, the
    /// <paramref name="caller"/>, and the work: its run <paramref name="workId"/> and its <paramref name="task"/>, if any.
    /// Validation has already rejected any that cannot be filled.
    /// </summary>
    public static string Fill(
        string text, ProjectOptions project, string agentName, AgentDefinition agent, DateTimeOffset? now = null, Caller? caller = null, string? workId = null,
        BoardTask? task = null) =>
        Expression().Replace(text, placeholder => Resolve(placeholder, project, agentName, agent, now, caller, workId, task)
            ?? throw new InvalidOperationException($"Placeholder {placeholder.Value} cannot be filled."));

    /// <summary>The problems of one text's placeholders. <paramref name="now"/> is any time for an operating fact, and null for instructions.</summary>
    private static IEnumerable<ConfigurationError> Check(
        string text, string path, ProjectOptions project, string agentName, AgentDefinition agent, DateTimeOffset? now, Caller? caller = null, string? workId = null)
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
            else if (now is null && Volatile.Contains(ns))
            {
                // CTX-02: the stable prefix does not depend on the caller, the work or the time.
                yield return new(ValidationPhase.Prefix, path, $"placeholder {placeholder.Value} is not allowed in instructions.",
                    "Instructions are the same for every call, so they cannot use caller, work or time values.");
            }
            else if (Resolve(placeholder, project, agentName, agent, now, caller, workId, null) is null)
            {
                var problem = $"placeholder {placeholder.Value} cannot be filled; it is never replaced with an empty string.";
                yield return new(ValidationPhase.References, path, problem,
                    ns switch
                    {
                        "project" => "Set the value it names: project.name or project.values.<name>.",
                        "agent" => "Use agent.name, or give the agent a description for agent.description.",
                        "now" => "Use {{now}} or {{now:date}}.",
                        "caller" => "Use caller.id, caller.tenant or caller.attributes.<name>.",
                        "work" => "Use work.id, or work.task.id, work.task.title or work.task.status.",
                        _ => now is null ? "Use the project or agent namespace in instructions." : "Use the project, agent, caller, work or now namespace in operating facts.",
                    });
            }
        }
    }

    private static string? Resolve(
        Match placeholder, ProjectOptions project, string agentName, AgentDefinition agent, DateTimeOffset? now, Caller? caller, string? workId, BoardTask? task) =>
        (placeholder.Groups["namespace"].Value, placeholder.Groups["name"].Value.Split('.'), placeholder.Groups["format"].Value) switch
        {
            ("project", ["name"], _) => project.Name,
            ("project", ["values", var key], _) => project.Values.GetValueOrDefault(key),
            ("agent", ["name"], _) => agentName,
            ("agent", ["description"], _) => agent.Description,
            ("now", [""], "") => now?.ToString("u", CultureInfo.InvariantCulture),
            ("now", [""], "date") => now?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ("caller", ["id"], "") => caller is null ? null : caller.Id ?? "anonymous",
            ("caller", ["tenant"], "") => caller is null ? null : caller.Tenant ?? "none",
            ("caller", ["attributes", var key], "") => caller is null ? null : caller.Attributes.GetValueOrDefault(key) ?? "none",
            ("work", ["id"], "") => workId,
            ("work", ["task", var field], "") when workId is not null => field switch
            {
                "id" => task?.Id ?? "none",
                "title" => task?.Title ?? "none",
                "status" => task?.State.ToString() ?? "none",
                _ => null,
            },
            _ => null,
        };

    // Text between {{ and }} that is not a dotted name, such as a template example, is left as it is.
    [GeneratedRegex(@"\{\{(?<namespace>[A-Za-z_][A-Za-z0-9_-]*)(\.(?<name>[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*))?(:(?<format>[A-Za-z0-9_-]+))?\}\}")]
    private static partial Regex Expression();
}
