namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>Applies <see cref="DependencyRules"/> to every project under <c>src/</c> and <c>tests/</c> of a repository.</summary>
internal static class RepositoryCheck
{
    // This project spells out the forbidden names, so its own source is not scanned.
    private static readonly string Self = typeof(RepositoryCheck).Assembly.GetName().Name!;

    public static IReadOnlyList<string> Run(string root)
    {
        var violations = new List<string>();
        foreach (var projectFile in ProjectFiles(root))
        {
            var name = Path.GetFileNameWithoutExtension(projectFile);
            var directory = Path.GetDirectoryName(projectFile)!;
            var assets = Path.Combine(directory, "obj", "project.assets.json");
            if (!File.Exists(assets))
            {
                violations.Add($"{name} has not been restored, so its dependencies cannot be checked. Run dotnet restore.");
                continue;
            }

            violations.AddRange(DependencyRules.Check(ProjectDependencies.Load(assets)));
            if (name != Self)
            {
                foreach (var source in SourceFiles(directory))
                {
                    violations.AddRange(DependencyRules.CheckSource(Path.GetRelativePath(root, source), File.ReadAllText(source)));
                }
            }
        }

        return violations;
    }

    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sleepyshark.Officina.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    private static readonly string[] ProjectFolders = ["src", "tests"];

    private static IEnumerable<string> ProjectFiles(string root) =>
        ProjectFolders
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Where(path => !IsBuildOutput(Path.GetRelativePath(root, path)))
            .Order(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(string projectDirectory) =>
        Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(projectDirectory, path)));

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(segment => segment is "bin" or "obj");
}
