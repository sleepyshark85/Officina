using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Placeholders;
using Sleepyshark.Officina.Core.Configuration.Validation;
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

    private static readonly StablePrefixValues Values = new(Project, "developer", new AgentDefinition { Instructions = "x", Description = "Writes code." });

    [Fact]
    public void Placeholders_are_found_with_their_namespace_name_and_format()
    {
        var found = Placeholder.FindAll("Run {{project.values.testCommand}} on {{now:date}}. {{ not one }} {{agent.name}}");

        Assert.Equal(
            [("project", "values.testCommand", (string?)null), ("now", "", "date"), ("agent", "name", null)],
            found.Select(placeholder => (placeholder.Namespace, placeholder.Name, placeholder.Format)));
    }

    [Fact]
    public void Project_and_agent_values_fill_the_stable_prefix()
    {
        var text = PlaceholderRules.FillStablePrefix(
            "You are {{agent.name}} on {{project.name}}: {{agent.description}} Test with {{project.values.testCommand}}. Keep {{ this }}.", Values);

        Assert.Equal("You are developer on invoice-api: Writes code. Test with dotnet test. Keep {{ this }}.", text);
    }

    [Theory]
    [InlineData("{{caller.id}}")]
    [InlineData("{{work.task.title}}")]
    [InlineData("{{now:date}}")]
    [InlineData("{{now}}")]
    public void Volatile_placeholders_are_not_allowed_in_the_stable_prefix(string placeholder)
    {
        var error = Assert.Single(PlaceholderRules.Check($"Hello {placeholder}.", "agents.lead.instructions", PlaceholderScope.StablePrefix, Values));

        Assert.Equal(ValidationPhase.Prefix, error.Phase);
        Assert.Equal("agents.lead.instructions", error.Path);
        Assert.Equal($"placeholder {placeholder} is not allowed in the stable prefix.", error.Problem);
        Assert.Equal("Move it to context.operatingFacts (CTX-02, CFG-14).", error.Fix);
    }

    [Fact]
    public void Volatile_placeholders_are_allowed_in_the_volatile_context()
    {
        Assert.Empty(PlaceholderRules.Check("Caller: {{caller.id}}, today {{now:date}}, task {{work.id}}", "context.operatingFacts[0]", PlaceholderScope.Volatile, Values));
    }

    [Theory]
    [InlineData("{{project.values.buildCommand}}", "Add \"buildCommand\" to project.values.")]
    [InlineData("{{project.values.testComand}}", "Add \"testComand\" to project.values. Did you mean \"testCommand\"?")]
    [InlineData("{{agent.role}}", "The agent namespace has name and description.")]
    public void An_unknown_placeholder_is_an_error_never_an_empty_string(string placeholder, string fix)
    {
        var error = Assert.Single(PlaceholderRules.Check(placeholder, "agents.lead.instructions", PlaceholderScope.StablePrefix, Values));

        Assert.Equal(ValidationPhase.References, error.Phase);
        Assert.Equal(fix, error.Fix);
        Assert.Throws<InvalidOperationException>(() => PlaceholderRules.FillStablePrefix(placeholder, Values));
    }

    [Fact]
    public void A_placeholder_for_a_value_that_is_not_set_is_an_error()
    {
        var error = Assert.Single(PlaceholderRules.Check("{{project.name}}", "agents.a.instructions", PlaceholderScope.StablePrefix, new StablePrefixValues(new ProjectOptions())));

        Assert.Equal("Set project.name.", error.Fix);
    }

    [Fact]
    public void An_unknown_namespace_is_an_error_with_a_suggestion()
    {
        var error = Assert.Single(PlaceholderRules.Check("{{projet.name}}", "p", PlaceholderScope.StablePrefix, Values));

        Assert.EndsWith("Did you mean \"project\"?", error.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{{secret.GITHUB_TOKEN}}")]
    [InlineData("{{env.ANTHROPIC_API_KEY}}")]
    public void A_secret_placeholder_weakens_INV_06(string placeholder)
    {
        var error = Assert.Single(PlaceholderRules.Check(placeholder, "agents.a.instructions", PlaceholderScope.Volatile, Values));

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
}
