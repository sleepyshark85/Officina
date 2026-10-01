using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

/// <summary>Validation of the programmatic form (CFG-02, CFG-06). Files add parse, shape and merge errors; see the Hosting tests.</summary>
public class ValidationTests
{
    private static readonly AgentDefinition Extractor = new() { Instructions = "Extract the invoice number." };

    [Fact]
    public void The_smallest_configuration_is_valid() => Assert.Empty(WithAgent(Extractor).Validate());

    [Fact]
    public void Missing_instructions_are_reported_with_the_path_and_a_fix()
    {
        var error = Assert.Single(WithAgent(Extractor with { Instructions = " " }).Validate());

        Assert.Equal((ValidationPhase.Shape, "agents.extractor.instructions", "is required but not set. Add it; it has no default."), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void A_missing_model_profile_or_provider_is_reported_with_what_exists()
    {
        var options = WithAgent(Extractor with { Model = "strnog" }) with
        {
            Models = new Dictionary<string, ModelProfile> { ["default"] = new(), ["other"] = new() { Provider = "claud" } },
        };

        var errors = options.Validate();

        Assert.All(errors, error => Assert.Equal(ValidationPhase.References, error.Phase));
        Assert.Equal(
            [("models.other.provider", "provider \"claud\" does not exist."), ("agents.extractor.model", "model profile \"strnog\" does not exist.")],
            errors.Select(error => (error.Path, error.Problem)));
        Assert.Equal("Add it to models, or use one of: default, other.", errors[1].Fix);
    }

    [Fact]
    public void A_secret_reference_needs_a_name() => Assert.Throws<ArgumentNullException>(() => new SecretReference(null!));

    [Theory]
    [InlineData("my.agent")]
    [InlineData("agent[0]")]
    public void Names_cannot_contain_the_characters_of_setting_paths(string name)
    {
        var options = new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { [name] = Extractor } };

        var error = Assert.Single(options.Validate());

        Assert.Equal((ValidationPhase.Shape, $"\"{name}\" is not a valid name."), (error.Phase, error.Problem));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_budget_of_zero_or_less_weakens_INV_07(int cost)
    {
        var options = new OfficinaOptions { Run = new RunDefaults { Budget = new RunBudget { Cost = cost } } };
        var error = Assert.Single(options.Validate());

        Assert.Equal((ValidationPhase.Invariants, "run.budget.cost"), (error.Phase, error.Path));
        Assert.Equal("must be greater than zero. A limit can be high, but never zero, negative or unlimited.", error.Problem);
    }

    [Fact]
    public void A_budget_removed_in_code_weakens_INV_07()
    {
        var error = Assert.Single(new OfficinaOptions { Run = new RunDefaults { Budget = null! } }.Validate());

        Assert.Equal((ValidationPhase.Invariants, "run.budget"), (error.Phase, error.Path));
    }

    [Fact]
    public void A_range_is_checked_wherever_the_setting_is()
    {
        var options = new OfficinaOptions { Models = new Dictionary<string, ModelProfile> { ["default"] = new() { MaxOutputTokens = 0 } } };

        var error = Assert.Single(options.Validate());

        Assert.Equal(("models.default.maxOutputTokens", "must be at least 1."), (error.Path, error.Problem));
    }

    [Fact]
    public void Every_error_is_reported_ordered_by_phase()
    {
        var options = WithAgent(Extractor with { Instructions = "Hello {{caller.id}}", Model = "missing" }) with
        {
            Run = new RunDefaults { Budget = new RunBudget { Time = TimeSpan.Zero } },
        };

        var errors = options.Validate();

        Assert.Equal([ValidationPhase.References, ValidationPhase.Prefix, ValidationPhase.Invariants], errors.Select(error => error.Phase));
        Assert.Equal("must be greater than zero. A limit can be high, but never zero, negative or unlimited.", errors[2].Problem);
    }

    [Fact]
    public void An_error_reads_as_location_path_problem_and_fix()
    {
        var error = new ConfigurationError(ValidationPhase.References, "agents.dev.model", "model profile \"x\" does not exist.", "Add it to models.")
        {
            Location = "sof.json:4:7",
        };

        Assert.Equal("sof.json:4:7: agents.dev.model: model profile \"x\" does not exist. Add it to models.", error.ToString());
    }

    private static OfficinaOptions WithAgent(AgentDefinition agent) =>
        new() { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = agent } };
}
