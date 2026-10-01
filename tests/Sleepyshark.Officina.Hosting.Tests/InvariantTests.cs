using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>
/// Attempts to weaken an invariant through the settings that exist so far are rejected with a message naming the
/// setting (CFG-17). Later slices add the cases for the settings they bring.
/// </summary>
public sealed class InvariantTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Fact]
    public void INV_06_a_secret_placeholder_in_instructions_is_rejected()
    {
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "Use {{secret.GITHUB_TOKEN}}." } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Invariants, "agents.a.instructions"), (error.Phase, error.Path));
    }

    [Theory]
    [InlineData("""{ "run": { "budget": null } }""", ValidationPhase.Shape, "run.budget", "cannot be removed with null.")]
    [InlineData("""{ "run": { "budget": { "cost": null } } }""", ValidationPhase.Shape, "run.budget.cost", "cannot be removed with null.")]
    [InlineData("""{ "run": { "budget": { "cost": 0 } } }""", ValidationPhase.Invariants, "run.budget.cost", "is 0, but must be greater than 0.")]
    [InlineData(
        """{ "run": { "budget": { "time": "0s" } } }""",
        ValidationPhase.Invariants,
        "run.budget.time",
        "is 0s, but must be greater than 0s.")]
    public void INV_07_a_budget_cannot_be_removed_or_zero(string text, ValidationPhase phase, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((phase, path, problem), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void INV_07_an_unlimited_budget_is_not_a_value()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": "unlimited", "time": "forever" } } }""");

        Assert.Equal(["run.budget.cost", "run.budget.time"], folder.Load().Errors.Select(error => error.Path).Order());
    }
}
