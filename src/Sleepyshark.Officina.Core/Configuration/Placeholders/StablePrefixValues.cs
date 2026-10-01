using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Core.Configuration.Placeholders;

/// <summary>The values placeholders in the stable prefix may use: the project's, and the agent definition's own (CFG-14).</summary>
public sealed class StablePrefixValues
{
    private readonly ProjectOptions project;
    private readonly string? agentName;
    private readonly AgentDefinition? agent;

    public StablePrefixValues(ProjectOptions project, string? agentName = null, AgentDefinition? agent = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        this.project = project;
        this.agentName = agentName;
        this.agent = agent;
    }

    /// <summary>The value, or null when the placeholder names something that is not set.</summary>
    public string? Resolve(Placeholder placeholder)
    {
        ArgumentNullException.ThrowIfNull(placeholder);
        return (placeholder.Namespace, placeholder.Name.Split('.')) switch
        {
            ("project", ["name"]) => project.Name,
            ("project", ["values", var key]) => project.Values.GetValueOrDefault(key),
            ("agent", ["name"]) => agentName,
            ("agent", ["description"]) => agent?.Description,
            _ => null,
        };
    }

    internal string FixFor(Placeholder placeholder) => (placeholder.Namespace, placeholder.Name.Split('.')) switch
    {
        ("project", ["name"]) => "Set project.name.",
        ("project", ["values", var key]) => $"Add \"{key}\" to project.values.{Suggestions.DidYouMean(key, project.Values.Keys)}",
        ("agent", ["description"]) => "Give the agent a description.",
        ("project", _) => "The project namespace has name and values.<name>.",
        _ => "The agent namespace has name and description.",
    };
}
