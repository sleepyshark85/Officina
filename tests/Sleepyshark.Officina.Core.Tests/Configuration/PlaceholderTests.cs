using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

/// <summary>The placeholder rules (CFG-14).</summary>
public class PlaceholderTests
{
    private static readonly ProjectOptions Project = new()
    {
        Name = "invoice-api",
        Values = new Dictionary<string, string> { ["testCommand"] = "dotnet test" },
    };

    private static readonly AgentDefinition Developer = new() { Instructions = "x", Description = "Writes code." };

    [Fact]
    public void Project_and_agent_values_fill_the_stable_prefix()
    {
        var text = Placeholder.Fill(
            "You are {{agent.name}} on {{project.name}}: {{agent.description}} Test with {{project.values.testCommand}}. Keep {{ this }}.",
            Project, "developer", Developer);

        Assert.Equal("You are developer on invoice-api: Writes code. Test with dotnet test. Keep {{ this }}.", text);
    }

    [Theory]
    [InlineData("{{caller.id}}")]
    [InlineData("{{work.task.title}}")]
    [InlineData("{{now:date}}")]
    [InlineData("{{now}}")]
    public void Volatile_placeholders_are_not_allowed_in_the_stable_prefix(string placeholder)
    {
        var error = Assert.Single(Check($"Hello {placeholder}."));

        Assert.Equal((ValidationPhase.Prefix, "agents.developer.instructions"), (error.Phase, error.Path));
        Assert.Equal($"placeholder {placeholder} is not allowed in the stable prefix.", error.Problem);
        Assert.Equal("Move it to context.operatingFacts (CTX-02, CFG-14).", error.Fix);
    }

    [Theory]
    [InlineData("{{project.values.buildCommand}}")]
    [InlineData("{{agent.role}}")]
    [InlineData("{{tool.read_file}}")]
    public void An_unknown_placeholder_is_an_error_never_an_empty_string(string placeholder)
    {
        var error = Assert.Single(Check(placeholder));

        Assert.Equal(ValidationPhase.References, error.Phase);
        Assert.Throws<InvalidOperationException>(() => Placeholder.Fill(placeholder, Project, "developer", Developer));
    }

    [Fact]
    public void A_placeholder_for_a_value_that_is_not_set_is_an_error()
    {
        var error = Assert.Single(Placeholder.Check("{{project.name}}", "p", new ProjectOptions(), "a", Developer));

        Assert.StartsWith("Set the project value", error.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{{secret.GITHUB_TOKEN}}")]
    [InlineData("{{env.ANTHROPIC_API_KEY}}")]
    public void A_secret_placeholder_weakens_INV_06(string placeholder)
    {
        var error = Assert.Single(Check(placeholder));

        Assert.Equal(ValidationPhase.Invariants, error.Phase);
        Assert.Contains("INV-06", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_runner_fills_placeholders_before_the_model_sees_the_instructions()
    {
        var kit = new TestKit(new OfficinaOptions
        {
            Project = Project,
            Agents = new Dictionary<string, AgentDefinition> { ["tester"] = new() { Instructions = "Run {{project.values.testCommand}} as {{agent.name}}." } },
        });
        kit.Model.Reply("ok");

        await kit.RunAsync("tester", "go", TestContext.Current.CancellationToken);

        Assert.Equal("Run dotnet test as tester.", Assert.Single(kit.Model.Requests).Instructions);
    }

    private static ConfigurationError[] Check(string text) =>
        Placeholder.Check(text, "agents.developer.instructions", Project, "developer", Developer).ToArray();
}
