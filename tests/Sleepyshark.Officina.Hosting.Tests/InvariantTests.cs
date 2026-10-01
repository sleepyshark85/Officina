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
    [InlineData("""{ "run": { "budget": { "cost": 0 } } }""", "run.budget.cost", "must be greater than zero. A limit can be high, but never zero, negative or unlimited.")]
    [InlineData("""{ "run": { "budget": { "time": "00:00:00" } } }""", "run.budget.time", "must be greater than zero. A limit can be high, but never zero, negative or unlimited.")]
    public void INV_07_a_budget_cannot_be_zero(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Invariants, path, problem), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void INV_07_null_does_not_remove_a_budget()
    {
        folder.Write("sof.json", """{ "run": { "budget": null } }""");

        var configuration = folder.Load();

        Assert.True(configuration.IsValid, string.Join('\n', configuration.Errors));
        Assert.Equal(new RunBudget(), configuration.Options.Run.Budget);
    }

    [Fact]
    public void INV_07_an_unlimited_budget_is_not_a_value()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": "unlimited" } } }""");

        Assert.Equal(ValidationPhase.Shape, Assert.Single(folder.Load().Errors).Phase);
    }
}
