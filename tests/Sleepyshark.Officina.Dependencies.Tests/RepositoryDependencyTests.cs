using System.Xml.Linq;

namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>TEST-32 against this repository.</summary>
public class RepositoryDependencyTests
{
    private static readonly string[] DesignedProjects =
    [
        "Core", "Team", "Workspace", "Sandbox", "Capabilities",
        "Storage.Sqlite", "Mcp", "Providers.Claude", "Testing", "Cli",
    ];

    [Fact]
    public void The_solution_follows_the_dependency_rules()
    {
        var violations = RepositoryCheck.Run(RepositoryCheck.FindRoot());

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void The_solution_has_every_project_from_the_design()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryCheck.FindRoot(), "Sleepyshark.Officina.slnx"));
        var projects = solution.Descendants("Project")
            .Select(project => Path.GetFileNameWithoutExtension(project.Attribute("Path")!.Value))
            .ToHashSet();

        Assert.All(DesignedProjects, name => Assert.Contains("Sleepyshark.Officina." + name, projects));
    }
}
