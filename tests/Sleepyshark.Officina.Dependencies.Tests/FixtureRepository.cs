using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>A throwaway repository with projects restored as NuGet would restore them, for proving that the check catches violations.</summary>
internal sealed class FixtureRepository : IDisposable
{
    public FixtureRepository()
    {
        Root = Directory.CreateTempSubdirectory("officina-deps-").FullName;
    }

    public string Root { get; }

    /// <param name="name">The project name, such as <c>Sleepyshark.Officina.Claude</c>.</param>
    /// <param name="references">What the project references itself: package ids, or names of other fixture projects.</param>
    /// <param name="graph">What each package or project in the restore graph depends on.</param>
    public FixtureRepository AddProject(string name, string[] references, Dictionary<string, string[]>? graph = null)
    {
        graph ??= [];
        var directory = Path.Combine(Root, "src", name);
        Directory.CreateDirectory(Path.Combine(directory, "obj"));
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var isProject = (string id) => id.StartsWith("Sleepyshark.", StringComparison.Ordinal);
        var libraries = new JsonObject();
        foreach (var id in references.Concat(graph.Keys).Concat(graph.Values.SelectMany(dependencies => dependencies)).Distinct())
        {
            libraries[id + "/1.0.0"] = new JsonObject
            {
                ["type"] = isProject(id) ? "project" : "package",
                ["dependencies"] = ToObject(graph.GetValueOrDefault(id, []), _ => "1.0.0"),
            };
        }

        var assets = new JsonObject
        {
            ["version"] = 3,
            ["targets"] = new JsonObject { ["net10.0"] = libraries },
            ["project"] = new JsonObject
            {
                ["restore"] = new JsonObject
                {
                    ["projectName"] = name,
                    ["frameworks"] = new JsonObject
                    {
                        ["net10.0"] = new JsonObject
                        {
                            ["projectReferences"] = ToObject(
                                references.Where(isProject).Select(id => Path.Combine(Root, "src", id, id + ".csproj")),
                                path => new JsonObject { ["projectPath"] = path }),
                        },
                    },
                },
                ["frameworks"] = new JsonObject
                {
                    ["net10.0"] = new JsonObject
                    {
                        ["dependencies"] = ToObject(
                            references.Where(id => !isProject(id)),
                            _ => new JsonObject { ["target"] = "Package", ["version"] = "[1.0.0, )" }),
                        ["frameworkReferences"] = new JsonObject { ["Microsoft.NETCore.App"] = new JsonObject() },
                    },
                },
            },
        };

        File.WriteAllText(Path.Combine(directory, "obj", "project.assets.json"), assets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return this;
    }

    public FixtureRepository AddSource(string project, string fileName, string text)
    {
        File.WriteAllText(Path.Combine(Root, "src", project, fileName), text);
        return this;
    }

    public FixtureRepository AddUnrestoredProject(string name)
    {
        var directory = Path.Combine(Root, "src", name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return this;
    }

    public IReadOnlyList<string> Check() => RepositoryCheck.Run(Root);

    public void Dispose() => Directory.Delete(Root, recursive: true);

    private static JsonObject ToObject(IEnumerable<string> keys, Func<string, JsonNode> value) =>
        new(keys.Select(key => KeyValuePair.Create(key, (JsonNode?)value(key))));
}
