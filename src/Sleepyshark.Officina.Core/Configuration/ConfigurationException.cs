namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Thrown when something is asked of a configuration that does not allow it: it has errors, or names no such agent.</summary>
public sealed class ConfigurationException(IReadOnlyList<ConfigurationError> errors)
    : InvalidOperationException("The configuration is not valid:" + string.Concat(errors.Select(error => Environment.NewLine + "  " + error)))
{
    public IReadOnlyList<ConfigurationError> Errors { get; } = errors;

    public static ConfigurationException UnknownAgent(string name, IEnumerable<string> agents)
    {
        var known = agents.Order(StringComparer.Ordinal).ToArray();
        return new([new ConfigurationError(ValidationPhase.References, $"agents.{name}", "does not exist.",
            known.Length == 0 ? "The configuration has no agents." : $"Use one of: {string.Join(", ", known)}.")]);
    }
}
