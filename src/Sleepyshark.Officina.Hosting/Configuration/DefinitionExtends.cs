using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Resolves <c>extends</c> between agent definitions (CFG-05): the base definition is the lower layer, under the same
/// merge rules, and inherited values say where they came from. Missing bases and cycles are merge errors.
/// </summary>
internal sealed class DefinitionExtends(LayerMerger merger, OriginMap origins, ConfigurationErrors errors)
{
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
        var baseName = extends.GetValueKind() == JsonValueKind.String ? extends.GetValue<string>() : null;
        definition.Remove("extends");
        origins.Take(path);
        if (baseName is null || baseName == name || visiting.Contains(baseName) || agents[baseName] is not JsonObject)
        {
            var problem = baseName is null ? "is not the name of an agent definition."
                : agents[baseName] is JsonObject ? $"agent definitions {string.Join(" → ", visiting.SkipWhile(item => item != baseName).Append(name).Append(baseName))} extend each other in a cycle."
                : $"agent definition \"{baseName}\" does not exist.";
            errors.Add(ValidationPhase.Merge, path, problem, "Name another agent definition, without cycles.", origins.LocationOf(path));
            errors.Reject($"agents.{name}");
            return;
        }

        visiting.Add(name);
        Resolve(agents, baseName, resolved, visiting);
        visiting.Remove(name);

        // The base's values come first, marked as inherited; the definition's own values merge on top.
        var own = origins.Take($"agents.{name}");
        foreach (var (basePath, origin) in origins.Take($"agents.{baseName}"))
        {
            origins.Set(basePath, origin);
            origins.Set($"agents.{name}{basePath[$"agents.{baseName}".Length..]}", origin with { Via = origin.Via ?? $"agents.{baseName}" });
        }

        var combined = agents[baseName]!.DeepClone().AsObject();
        merger.Merge(combined, definition, null, $"agents.{name}", ownPath => own.GetValueOrDefault(ownPath) ?? ConfigOrigin.CodeDefault);
        agents[name] = combined;
    }
}
