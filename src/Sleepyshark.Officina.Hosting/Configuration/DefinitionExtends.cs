using Microsoft.Extensions.Configuration;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Resolves <c>extends</c> between agent definitions (CFG-05): a definition inherits every key of its base that it does
/// not set itself. The inherited keys become the lowest configuration provider; each remembers where the base set it.
/// Missing bases and cycles are merge errors.
/// </summary>
internal sealed class DefinitionExtends(IConfigurationRoot configuration, Func<string, ConfigurationOrigin?> originOf)
{
    private const string Fix = "Name another agent definition, without cycles.";

    private readonly HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> visiting = [];

    /// <summary>The inherited keys and their values.</summary>
    public Dictionary<string, string?> Inherited { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each inherited value was set in its base definition.</summary>
    public Dictionary<string, ConfigurationOrigin> Origins { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ConfigurationError> Errors { get; } = [];

    public DefinitionExtends Resolve()
    {
        foreach (var agent in configuration.GetSection("agents").GetChildren())
        {
            Resolve(agent.Key);
        }

        return this;
    }

    private void Resolve(string name)
    {
        var path = $"agents:{name}:extends";
        if (!resolved.Add(name) || configuration[path] is not { } baseName)
        {
            return;
        }

        if (!configuration.GetSection($"agents:{baseName}").Exists())
        {
            Reject(path, $"agent definition \"{baseName}\" does not exist.");
            return;
        }

        if (string.Equals(baseName, name, StringComparison.OrdinalIgnoreCase) || visiting.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            var start = visiting.SkipWhile(item => !string.Equals(item, baseName, StringComparison.OrdinalIgnoreCase));
            var cycle = string.Join(" → ", start.Append(name).Append(baseName));
            Reject(path, $"agent definitions {cycle} extend each other in a cycle.");
            return;
        }

        visiting.Add(name);
        Resolve(baseName);
        visiting.Remove(name);
        Inherit(name, baseName);
    }

    private void Inherit(string name, string baseName)
    {
        var basePrefix = $"agents:{baseName}:";
        var own = configuration.GetSection($"agents:{name}");
        var baseValues = configuration.GetSection($"agents:{baseName}").AsEnumerable(makePathsRelative: true)
            .Concat(Inherited.Where(entry => entry.Key.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(entry => KeyValuePair.Create(entry.Key[basePrefix.Length..], entry.Value)))
            .Where(entry => entry.Value is not null && !string.Equals(entry.Key, "extends", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var (relative, value) in baseValues)
        {
            if (own.GetSection(relative).Exists())
            {
                continue;
            }

            var key = $"agents:{name}:{relative}";
            Inherited[key] = value;
            var origin = Origins.GetValueOrDefault(basePrefix + relative) ?? originOf(basePrefix + relative) ?? ConfigurationOrigin.CodeDefault;
            Origins[key] = origin with { Via = origin.Via ?? $"agents.{baseName}" };
        }
    }

    private void Reject(string path, string problem) =>
        Errors.Add(new ConfigurationError(ValidationPhase.Merge, path.Replace(':', '.'), problem, Fix) { Location = originOf(path)?.Location });
}
