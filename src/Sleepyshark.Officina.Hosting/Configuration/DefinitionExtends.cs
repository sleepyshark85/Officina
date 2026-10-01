using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Resolves <c>extends</c> between agent definitions (CFG-05): the base definition is the lower layer, under the same
/// merge rules, and inherited values say where they came from. Missing bases and cycles are merge errors.
/// </summary>
internal sealed class DefinitionExtends(LayerMerger merger, OriginMap origins, LoadErrors errors)
{
    private const string Fix = "Name another agent definition, without cycles.";

    public void Resolve(JsonObject root)
    {
        if (root["agents"] is not JsonObject agents)
        {
            return;
        }

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new List<string>();
        foreach (var name in agents.Select(entry => entry.Key).ToArray())
        {
            Resolve(agents, name, resolved, visiting);
        }
    }

    private void Resolve(JsonObject agents, string name, HashSet<string> resolved, List<string> visiting)
    {
        if (!resolved.Add(name) || agents[name] is not JsonObject definition || definition["extends"] is not { } extends)
        {
            return;
        }

        var path = $"agents.{name}.extends";
        var at = origins.LocationOf(path);
        definition.Remove("extends");
        origins.Take(path);

        if (extends.GetValueKind() != JsonValueKind.String)
        {
            Reject(name, path, "is not the name of an agent definition.", at);
            return;
        }

        var baseName = extends.GetValue<string>();
        if (agents[baseName] is not JsonObject)
        {
            Reject(name, path, $"agent definition \"{baseName}\" does not exist.", at);
            return;
        }

        if (baseName == name || visiting.Contains(baseName))
        {
            var cycle = string.Join(" → ", visiting.SkipWhile(item => item != baseName).Append(name).Append(baseName));
            Reject(name, path, $"agent definitions {cycle} extend each other in a cycle.", at);
            return;
        }

        visiting.Add(name);
        Resolve(agents, baseName, resolved, visiting);
        visiting.Remove(name);
        Inherit(agents, name, baseName, definition);
    }

    /// <summary>The base's values come first, marked as inherited; the definition's own values merge on top.</summary>
    private void Inherit(JsonObject agents, string name, string baseName, JsonObject definition)
    {
        var own = origins.Take($"agents.{name}");
        var basePrefix = $"agents.{baseName}";
        foreach (var (basePath, origin) in origins.Take(basePrefix))
        {
            origins.Set(basePath, origin);
            origins.Set($"agents.{name}{basePath[basePrefix.Length..]}", origin with { Via = origin.Via ?? basePrefix });
        }

        var combined = agents[baseName]!.DeepClone().AsObject();
        merger.Merge(combined, definition, null, $"agents.{name}", ownPath => own.GetValueOrDefault(ownPath) ?? ConfigurationOrigin.CodeDefault);
        agents[name] = combined;
    }

    private void Reject(string name, string path, string problem, ConfigurationOrigin? at)
    {
        errors.Add(ValidationPhase.Merge, path, problem, Fix, at);
        errors.Reject($"agents.{name}");
    }
}
