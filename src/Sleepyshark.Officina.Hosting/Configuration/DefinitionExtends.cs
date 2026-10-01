using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Resolves <c>extends</c> between agent definitions (CFG-05): the base definition is the lower layer,
/// with the same merge rules as layers. Missing bases and cycles are merge errors (phase 3).
/// </summary>
internal sealed class DefinitionExtends(ICollection<ConfigurationError> errors, ISet<string> badPaths)
{
    private const string Key = "extends";

    private readonly Dictionary<string, ConfigObject> resolved = new(StringComparer.Ordinal);
    private readonly List<string> visiting = [];
    private ConfigObject agents = null!;

    public ConfigObject Resolve(ConfigObject root)
    {
        if (root.Get("agents") is not ConfigObject definitions)
        {
            return root;
        }

        agents = definitions;
        foreach (var (name, definition) in agents.Properties)
        {
            if (definition is ConfigObject)
            {
                ResolveAgent(name);
            }
        }

        // An entry removed with null stays as it is; binding skips it.
        var resolvedAgents = new ConfigObject(agents.Origin, agents.Properties.Select(entry =>
            KeyValuePair.Create(entry.Key, resolved.TryGetValue(entry.Key, out var definition) ? definition : entry.Value)));
        return new ConfigObject(root.Origin, root.Properties.Select(property => property.Key == "agents" ? KeyValuePair.Create("agents", (ConfigNode)resolvedAgents) : property));
    }

    private ConfigObject ResolveAgent(string name)
    {
        if (resolved.TryGetValue(name, out var done))
        {
            return done;
        }

        var definition = (ConfigObject)agents.Get(name)!;
        if (definition.Get(Key) is not ConfigScalar { Kind: JsonValueKind.String } extends)
        {
            return resolved[name] = definition;
        }

        var path = SettingPath.Child(SettingPath.Child("agents", name), Key);
        var baseName = extends.Raw;
        if (visiting.Contains(baseName) || baseName == name)
        {
            var cycle = visiting.Contains(baseName) ? visiting[visiting.IndexOf(baseName)..] : [];
            Error(path, extends, $"agent definitions {string.Join(" → ", cycle.Append(name).Append(baseName))} extend each other in a cycle.",
                "Remove extends from one of them.");
            foreach (var member in cycle.Append(name))
            {
                badPaths.Add(SettingPath.Child("agents", member));
            }

            return resolved[name] = definition.Without(Key);
        }

        if (agents.Get(baseName) is not ConfigObject)
        {
            Error(path, extends, $"agent definition \"{baseName}\" does not exist.",
                "Name another agent definition." + Suggestions.DidYouMean(baseName, agents.Properties.Select(entry => entry.Key)));
            badPaths.Add(SettingPath.Child("agents", name));
            return resolved[name] = definition.Without(Key);
        }

        visiting.Add(name);
        var baseDefinition = ResolveAgent(baseName);
        visiting.RemoveAt(visiting.Count - 1);
        if (resolved.TryGetValue(name, out var meanwhile))
        {
            // The cycle was found further down and already settled this definition.
            return meanwhile;
        }

        var inherited = (ConfigObject)Inherited(baseDefinition.Without(Key), SettingPath.Child("agents", baseName));
        var merged = (ConfigObject)LayerMerger.Merge(inherited, definition, null);
        return resolved[name] = new ConfigObject(definition.Origin, merged.Properties);
    }

    /// <summary>The base definition's values, marked as inherited so their origin says so (CFG-04).</summary>
    private static ConfigNode Inherited(ConfigNode node, string via)
    {
        var origin = node.Origin.Via is null ? node.Origin with { Via = via } : node.Origin;
        return node switch
        {
            ConfigObject obj => new ConfigObject(origin, obj.Properties.Select(property => KeyValuePair.Create(property.Key, Inherited(property.Value, via)))),
            ConfigArray array => new ConfigArray(origin, array.Items.Select(item => Inherited(item, via))),
            ConfigScalar scalar => new ConfigScalar(origin, scalar.Kind, scalar.Raw, scalar.Lenient),
            _ => new ConfigNull(origin),
        };
    }

    private void Error(string path, ConfigNode node, string problem, string fix)
    {
        errors.Add(new ConfigurationError(ValidationPhase.Merge, path, problem, fix) { Location = node.Origin.Location });
        badPaths.Add(path);
    }
}
