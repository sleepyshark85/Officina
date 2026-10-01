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
        var errors = Annotations(Run, "run", ValidationPhase.Invariants)
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
            errors = errors.Concat(Names([name], "agents")).Concat(Annotations(agent, $"agents.{name}")).Concat(ModelExists(name, agent));
        }

        return errors.Concat(InstructionPlaceholders.Check(this));
    }

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

    private static ConfigurationError Missing(string path, string kind, string name, string section, IEnumerable<string> known) =>
        new(ValidationPhase.References, path, $"{kind} \"{name}\" does not exist.",
            $"Add it to {section}, or use one of: {string.Join(", ", known.Order(StringComparer.Ordinal))}.");
}
