namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>The dependency check against this repository.</summary>
public class RepositoryDependencyTests
{
    [Fact]
    public void The_solution_follows_the_dependency_rules()
    {
        var violations = RepositoryCheck.Run(RepositoryCheck.FindRoot());

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
