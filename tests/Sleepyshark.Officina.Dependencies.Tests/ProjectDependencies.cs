using System.Collections.Immutable;
using System.Text.Json;

namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>A project's resolved dependency graph, read from the <c>project.assets.json</c> that restore writes.</summary>
/// <param name="Name">The project name.</param>
/// <param name="Direct">The packages and projects the project references itself.</param>
/// <param name="Graph">Every package and project in the restore graph, with what each depends on.</param>
/// <param name="FrameworkReferences">Shared frameworks the project references, such as <c>Microsoft.NETCore.App</c>.</param>
internal sealed record ProjectDependencies(
    string Name,
    ImmutableHashSet<string> Direct,
    ImmutableDictionary<string, ImmutableArray<string>> Graph,
    ImmutableHashSet<string> FrameworkReferences)
{
    private static readonly StringComparer Ids = StringComparer.OrdinalIgnoreCase;

    public static ProjectDependencies Load(string assetsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = document.RootElement;
        var project = root.GetProperty("project");
        var name = project.GetProperty("restore").GetProperty("projectName").GetString()!;

        var direct = ImmutableHashSet.CreateBuilder<string>(Ids);
        var frameworkReferences = ImmutableHashSet.CreateBuilder<string>(Ids);
        foreach (var framework in Values(project.GetProperty("frameworks")))
        {
            direct.UnionWith(Properties(framework, "dependencies").Select(dependency => dependency.Name));
            frameworkReferences.UnionWith(Properties(framework, "frameworkReferences").Select(reference => reference.Name));
        }

        foreach (var framework in Values(project.GetProperty("restore").GetProperty("frameworks")))
        {
            direct.UnionWith(Properties(framework, "projectReferences").Select(reference => Path.GetFileNameWithoutExtension(reference.Name)));
        }

        var graph = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(Ids);
        foreach (var target in Values(root.GetProperty("targets")))
        {
            foreach (var library in Properties(target))
            {
                var id = library.Name.Split('/')[0];
                graph[id] = [.. Properties(library.Value, "dependencies").Select(dependency => dependency.Name)];
            }
        }

        return new ProjectDependencies(name, direct.ToImmutable(), graph.ToImmutable(), frameworkReferences.ToImmutable());
    }

    /// <summary>Everything the project reaches, without walking past the given nodes. The stop nodes themselves are included.</summary>
    public ImmutableHashSet<string> Reachable(Func<string, bool> stopAt)
    {
        var seen = ImmutableHashSet.CreateBuilder<string>(Ids);
        var pending = new Stack<string>(Direct);
        while (pending.TryPop(out var id))
        {
            if (!seen.Add(id) || stopAt(id))
            {
                continue;
            }

            foreach (var dependency in Graph.GetValueOrDefault(id, []))
            {
                pending.Push(dependency);
            }
        }

        return seen.ToImmutable();
    }

    private static IEnumerable<JsonProperty> Properties(JsonElement element, string property) =>
        element.TryGetProperty(property, out var child) ? Properties(child) : [];

    private static IReadOnlyList<JsonProperty> Properties(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object ? [.. element.EnumerateObject()] : [];

    private static IEnumerable<JsonElement> Values(JsonElement element) => Properties(element).Select(child => child.Value);
}
