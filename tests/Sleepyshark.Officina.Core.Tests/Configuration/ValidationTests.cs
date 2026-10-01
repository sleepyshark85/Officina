using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

/// <summary>Validation of the programmatic form (CFG-02, CFG-06). Files add parse, shape and merge errors; see the Hosting tests.</summary>
public class ValidationTests
{
    private static readonly AgentDefinition Extractor = new() { Instructions = "Extract the invoice number." };

    [Fact]
    public void The_smallest_configuration_is_valid() => Assert.Empty(ConfigurationValidator.Validate(WithAgent(Extractor)));

    [Fact]
    public void Missing_instructions_are_reported_with_the_path_and_a_fix()
    {
        var error = Assert.Single(ConfigurationValidator.Validate(WithAgent(Extractor with { Instructions = " " })));

        Assert.Equal((ValidationPhase.Shape, "agents.extractor.instructions", "is required but not set."), (error.Phase, error.Path, error.Problem));
        Assert.StartsWith("Add \"instructions\" to agents.extractor", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_names_are_reported_with_what_exists()
    {
        var options = WithAgent(Extractor with { Model = "strnog" }) with
        {
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new() { Fallbacks = ["default", "missing"] },
                ["other"] = new() { Provider = "claud" },
            },
        };

        var errors = ConfigurationValidator.Validate(options);

        Assert.All(errors, error => Assert.Equal(ValidationPhase.References, error.Phase));
        Assert.Equal(
            [
                ("models.default.fallbacks[0]", "a profile cannot be its own fallback."),
                ("models.default.fallbacks[1]", "model profile \"missing\" does not exist."),
                ("models.other.provider", "provider \"claud\" does not exist."),
                ("agents.extractor.model", "model profile \"strnog\" does not exist."),
            ],
            errors.Select(error => (error.Path, error.Problem)));
        Assert.Equal("Add it to models, or use one of: default, other.", errors[3].Fix);
    }

    [Theory]
    [InlineData("Use the key sk-ant-api03-abcdefghijklmnop to call the API.")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    public void A_value_that_looks_like_a_credential_is_rejected_without_repeating_it(string text)
    {
        var error = Assert.Single(ConfigurationValidator.Validate(WithAgent(Extractor with { Instructions = text })));

        Assert.Equal((ValidationPhase.Shape, "agents.extractor.instructions"), (error.Phase, error.Path));
        Assert.Contains("{ \"secret\": \"NAME\" }", error.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(text, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_under_a_credential_name_is_rejected()
    {
        using var settings = JsonDocument.Parse("""{ "password": "letmein", "nested": { "token": "abc" }, "maxTokens": 100 }""");
        var options = new OfficinaOptions
        {
            Project = new ProjectOptions { Values = new Dictionary<string, string> { ["apiToken"] = "hunter2", ["maxTokens"] = "100" } },
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new() { Settings = settings.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()) },
            },
        };

        var errors = ConfigurationValidator.Validate(options);

        Assert.Equal(["models.default.settings.nested.token", "models.default.settings.password", "project.values.apiToken"], errors.Select(error => error.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void An_invalid_secret_name_is_rejected()
    {
        var options = new OfficinaOptions { Providers = new Dictionary<string, ProviderOptions> { ["claude"] = new() { ApiKey = new SecretReference("MY KEY") } } };

        Assert.Equal("providers.claude.apiKey.secret", Assert.Single(ConfigurationValidator.Validate(options)).Path);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_budget_of_zero_or_less_weakens_INV_07(int cost)
    {
        var error = Assert.Single(ConfigurationValidator.Validate(new OfficinaOptions { Run = new RunDefaults { Budget = new RunBudget { Cost = cost } } }));

        Assert.Equal((ValidationPhase.Invariants, "run.budget.cost"), (error.Phase, error.Path));
        Assert.Contains("INV-07", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_removed_in_code_weakens_INV_07()
    {
        var error = Assert.Single(ConfigurationValidator.Validate(new OfficinaOptions { Run = new RunDefaults { Budget = null! } }));

        Assert.Equal((ValidationPhase.Invariants, "run.budget"), (error.Phase, error.Path));
    }

    [Fact]
    public void Every_error_is_reported_ordered_by_phase()
    {
        var options = WithAgent(Extractor with { Instructions = "Hello {{caller.id}}", Model = "missing" }) with
        {
            Run = new RunDefaults { Budget = new RunBudget { Time = TimeSpan.Zero } },
        };

        var errors = ConfigurationValidator.Validate(options);

        Assert.Equal([ValidationPhase.References, ValidationPhase.Prefix, ValidationPhase.Invariants], errors.Select(error => error.Phase));
        Assert.Equal("is 0s, but must be greater than 0s.", errors[2].Problem);
    }

    [Fact]
    public void An_error_reads_as_location_path_problem_and_fix()
    {
        var error = new ConfigurationError(ValidationPhase.References, "agents.dev.model", "model profile \"x\" does not exist.", "Add it to models.") { Location = "sof.json:4:7" };

        Assert.Equal("sof.json:4:7: agents.dev.model: model profile \"x\" does not exist. Add it to models.", error.ToString());
    }

    private static OfficinaOptions WithAgent(AgentDefinition agent) =>
        new() { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = agent } };
}
