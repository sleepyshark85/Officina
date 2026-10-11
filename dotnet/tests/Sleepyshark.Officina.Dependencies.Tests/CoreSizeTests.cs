namespace Sleepyshark.Officina.Dependencies.Tests;

/// <summary>The core stays small: growing past its line budget needs a reason and a change here.</summary>
public class CoreSizeTests
{
    private const int LineBudget = 3_500;

    [Fact]
    public void The_core_stays_within_its_line_budget()
    {
        var core = Path.Combine(RepositoryCheck.FindRoot(), "src", "Sleepyshark.Officina");
        var lines = Directory.EnumerateFiles(core, "*.cs", SearchOption.AllDirectories)
            .Where(file => !IsBuildOutput(Path.GetRelativePath(core, file)))
            .Sum(file => File.ReadLines(file).Count());

        Assert.True(lines <= LineBudget, $"The core has {lines} lines; the budget is {LineBudget}.");
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
