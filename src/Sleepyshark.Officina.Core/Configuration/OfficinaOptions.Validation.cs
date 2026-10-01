using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Validation of the whole configuration (CFG-06). The root sees every section, so it is the one entry point for files
/// and the programmatic form alike (CFG-02): each section's <c>[Required]</c> and <c>[Range]</c> annotations, then the
/// rules across sections.
/// </summary>
public sealed partial record OfficinaOptions : IValidatableObject
{
    /// <summary>Every error, ordered by phase.</summary>
    public IReadOnlyList<ConfigurationError> Validate() => [.. Errors().OrderBy(error => error.Phase)];

    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext) =>
        Validate().Select(error => new ValidationResult(error.ToString(), [error.Path]));

    private IEnumerable<ConfigurationError> Errors()
    {
        // INV-07: the budget is an invariant, so a value outside its range is an attempt to weaken it.
        var errors = FormatVersionSupported()
            .Concat(Annotations(Run, "run", ValidationPhase.Invariants))
            .Concat(Run.Budget is null ? [] : Annotations(Run.Budget, "run.budget", ValidationPhase.Invariants))
            .Concat(Names(Project.Values.Keys, "project.values"));

        foreach (var (name, provider) in Providers)
        {
            errors = errors.Concat(Names([name], "providers")).Concat(Annotations(provider, $"providers.{name}"))
                .Concat(provider.ApiKey is null ? [] : Annotations(provider.ApiKey, $"providers.{name}.apiKey"));
        }

        foreach (var (name, profile) in Models)
        {
            errors = errors.Concat(Names([name], "models")).Concat(Annotations(profile, $"models.{name}")).Concat(ProviderExists(name, profile));
        }

        foreach (var (name, agent) in Agents)
        {
            errors = errors.Concat(Names([name], "agents")).Concat(Annotations(agent, $"agents.{name}")).Concat(ModelExists(name, agent))
                .Concat(References($"agents.{name}.tools", "tool set", agent.Tools, "toolSets", ToolSets.Keys));
        }

        foreach (var (index, path) in (Capabilities.Workspace?.ProtectedPaths ?? []).Index())
        {
            errors = errors.Concat(Annotations(path, $"capabilities.workspace.protectedPaths[{index}]"));
        }

        return errors.Concat(ToolSettings()).Concat(InstructionPlaceholders.Check(this));
    }

    /// <summary>
    /// The tool settings that can be checked without the tools themselves. The tool pipeline checks the rest when it is
    /// built with the application's tools (TOOL-02).
    /// </summary>
    private IEnumerable<ConfigurationError> ToolSettings()
    {
        var errors = Names(Tools.Keys, "tools").Concat(Names(ToolSets.Keys, "toolSets")).Concat(Names(Gates.Keys, "gates"))
            .Concat(References("policies.gates", "gate", Policies.Gates, "gates", Gates.Keys));
        foreach (var (name, tool) in Tools)
        {
            errors = errors.Concat(Annotations(tool, $"tools.{name}")).Concat(References($"tools.{name}.gates", "gate", tool.Gates, "gates", Gates.Keys));
            if (tool.Source is not null && tool.ExtensionId() is null && tool.ProviderTool() is null)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"tools.{name}.source", $"\"{tool.Source}\" is not a tool source.",
                    "Use extension:<id> for a tool the application registers, or provider:<name> for a provider's own tool."));
            }
            else if (tool.ProviderTool() is not null && string.IsNullOrWhiteSpace(tool.Reason))
            {
                // TOOL-13: a provider tool skips the per-call steps, so it is enabled only explicitly, with a reason.
                errors = errors.Append(new(ValidationPhase.Tools, $"tools.{name}.reason", "is required for a provider tool.",
                    "Say why the agents need it; the provider runs it without gates or approval."));
            }
        }

        foreach (var (name, tools) in ToolSets)
        {
            errors = errors.Concat(References($"toolSets.{name}", "tool", tools, "tools", Tools.Keys));
        }

        foreach (var (name, gate) in Gates)
        {
            errors = errors.Concat(Annotations(gate, $"gates.{name}"));
            if (gate.Use is not null and not GateOptions.RequireApproval and not GateOptions.Deny && gate.ExtensionId() is null)
            {
                errors = errors.Append(new(ValidationPhase.Shape, $"gates.{name}.use", $"\"{gate.Use}\" is not a gate.",
                    $"Use {GateOptions.RequireApproval}, {GateOptions.Deny}, or extension:<id> for a gate the application registers."));
            }
        }

        foreach (var (index, rule) in Policies.PermissionRules.Index())
        {
            var path = $"policies.permissionRules[{index}]";
            errors = errors.Concat(Annotations(rule, path)).Concat(rule.Tool is null ? [] : References($"{path}.tool", "tool", [rule.Tool], "tools", Tools.Keys));
            if (rule.Action == PolicyAction.Route)
            {
                errors = errors.Concat(rule.To is null
                    ? [new(ValidationPhase.Shape, $"{path}.to", "is required to route.", "Name the agent the turn is handed to.")]
                    : References($"{path}.to", "agent", [rule.To], "agents", Agents.Keys));
            }
        }

        return errors;
    }


    private IEnumerable<ConfigurationError> FormatVersionSupported() =>
        FormatVersion == CurrentFormatVersion
            ? []
            : [new(ValidationPhase.Parse, "formatVersion", $"format version {FormatVersion} is not supported.",
                $"This core reads format version {CurrentFormatVersion}.")];

    /// <summary>The section's <c>[Required]</c> and <c>[Range]</c> annotations; each message is the problem and the fix.</summary>
    private static IEnumerable<ConfigurationError> Annotations(object section, string path, ValidationPhase phase = ValidationPhase.Shape)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(section, new ValidationContext(section), results, validateAllProperties: true);
        return results.Select(result => new ConfigurationError(
            phase, $"{path}.{JsonNamingPolicy.CamelCase.ConvertName(result.MemberNames.First())}", result.ErrorMessage!, ""));
    }

    private IEnumerable<ConfigurationError> ProviderExists(string name, ModelProfile profile) =>
        profile.Provider is null || Providers.ContainsKey(profile.Provider)
            ? []
            : [Missing($"models.{name}.provider", "provider", profile.Provider, "providers", Providers.Keys)];

    private IEnumerable<ConfigurationError> ModelExists(string name, AgentDefinition agent) =>
        agent.Model is null || Models.ContainsKey(agent.Model)
            ? []
            : [Missing($"agents.{name}.model", "model profile", agent.Model, "models", Models.Keys)];

    /// <summary>Names of named entries cannot contain the characters of setting paths.</summary>
    private static IEnumerable<ConfigurationError> Names(IEnumerable<string> names, string section) =>
        names.Where(name => name.IndexOfAny(['.', '[', ']']) >= 0)
            .Select(name => new ConfigurationError(
                ValidationPhase.Shape, $"{section}.{name}", $"\"{name}\" is not a valid name.", "Leave out '.', '[' and ']'."));

    private static IEnumerable<ConfigurationError> References(string path, string kind, IEnumerable<string> names, string section, IEnumerable<string> known) =>
        names.Where(name => !known.Contains(name)).Select(name => Missing(path, kind, name, section, known));

    private static ConfigurationError Missing(string path, string kind, string name, string section, IEnumerable<string> known) =>
        new(ValidationPhase.References, path, $"{kind} \"{name}\" does not exist.",
            $"Add it to {section}, or use one of: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
}
