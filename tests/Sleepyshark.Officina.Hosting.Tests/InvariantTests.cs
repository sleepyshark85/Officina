using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>
/// Attempts to weaken an invariant are rejected with a message naming the setting (CFG-17, TEST-04). Settings that
/// would weaken INV-02 to INV-10 and arrive in later slices fail today as unknown settings; the slices that add
/// them add their own rules. INV-01 needs the condition language, which arrives in S03.
/// </summary>
public sealed class InvariantTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("INV-02", """{ "agents": { "a": { "instructions": "x", "permissions": ["*"] } } }""", "agents.a.permissions")]
    [InlineData("INV-03", """{ "agents": { "a": { "instructions": "x", "identity": "admin" } } }""", "agents.a.identity")]
    [InlineData("INV-04", """{ "tools": { "create_issue": { "source": "extension:Acme.CreateIssue", "kind": "write" } } }""", "tools")]
    [InlineData("INV-05", """{ "operations": { "audit": { "enabled": false } } }""", "operations")]
    [InlineData("INV-08", """{ "agents": { "a": { "instructions": "x", "includeToolResults": true } } }""", "agents.a.includeToolResults")]
    [InlineData("INV-09", """{ "agents": { "a": { "instructions": "x", "output": { "onCheckFailure": "accept" } } } }""", "agents.a.output")]
    [InlineData("INV-10", """{ "capabilities": { "workspace": { "protectedPaths": [] } } }""", "capabilities")]
    public void A_setting_that_would_weaken_an_invariant_does_not_exist(string invariant, string text, string path)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.True((ValidationPhase.Shape, path) == (error.Phase, error.Path), $"{invariant}: {error}");
    }

    [Fact]
    public void INV_06_a_secret_used_as_a_literal_is_rejected()
    {
        folder.Write("sof.json", """{ "providers": { "claude": { "apiKey": "my-key-value" } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal("providers.claude.apiKey", error.Path);
        Assert.Contains("CFG-09", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void INV_06_a_secret_placeholder_in_instructions_is_rejected()
    {
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": "Use {{secret.GITHUB_TOKEN}}." } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Invariants, "agents.a.instructions"), (error.Phase, error.Path));
        Assert.Contains("INV-06", error.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "run": { "budget": null } }""", "run.budget", "cannot be removed with null.")]
    [InlineData("""{ "run": { "budget": { "cost": null } } }""", "run.budget.cost", "cannot be removed with null.")]
    [InlineData("""{ "run": { "budget": { "cost": 0 } } }""", "run.budget.cost", "is 0, but must be greater than 0.")]
    [InlineData("""{ "run": { "budget": { "time": "0s" } } }""", "run.budget.time", "is 0s, but must be greater than 0s.")]
    public void INV_07_a_budget_cannot_be_removed_or_zero(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Invariants, path, problem), (error.Phase, error.Path, error.Problem));
        Assert.Contains("INV-07", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void INV_07_an_unlimited_budget_is_not_a_value()
    {
        folder.Write("sof.json", """{ "run": { "budget": { "cost": "unlimited", "time": "forever" } } }""");

        Assert.Equal(["run.budget.cost", "run.budget.time"], folder.Load().Errors.Select(error => error.Path));
    }
}
