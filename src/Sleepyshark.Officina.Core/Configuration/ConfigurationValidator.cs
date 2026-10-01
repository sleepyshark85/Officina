namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Validates bound Options and reports every error, ordered by phase (CFG-06). Files add the parse, shape and merge
/// errors only they can have; the programmatic form gets everything else from here (CFG-02).
/// </summary>
public static class ConfigurationValidator
{
    public static IReadOnlyList<ConfigurationError> Validate(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var visits = SettingWalker.Walk(options).ToArray();
        var placeholders = options.Agents
            .Where(agent => agent.Value.Instructions is not null)
            .SelectMany(agent => Placeholder.Check(agent.Value.Instructions, $"agents.{agent.Key}.instructions", options.Project, agent.Key, agent.Value));
        return [.. SettingRules.Check(visits)
            .Concat(CredentialRule.Check(visits))
            .Concat(ReferenceRules.Check(options))
            .Concat(placeholders)
            .OrderBy(error => error.Phase)];
    }
}
