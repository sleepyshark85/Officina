using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

/// <summary>Validation of the programmatic form (CFG-02, CFG-06). The file form adds parse, shape and merge errors; see the Hosting tests.</summary>
public class ValidationTests
{
    private static readonly AgentDefinition Extractor = new() { Instructions = "Extract the invoice number." };

    [Fact]
    public void The_smallest_configuration_is_valid()
    {
        Assert.Empty(Validate(new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = Extractor } }));
    }

    [Fact]
    public void Missing_instructions_are_reported_with_the_path_and_a_fix()
    {
        var error = Assert.Single(Validate(WithAgent(Extractor with { Instructions = " " })));

        Assert.Equal(ValidationPhase.Shape, error.Phase);
        Assert.Equal("agents.extractor.instructions", error.Path);
        Assert.Equal("is required but not set.", error.Problem);
        Assert.StartsWith("Add \"instructions\" to agents.extractor.", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_model_profile_is_reported_with_a_suggestion()
    {
        var options = WithAgent(Extractor with { Model = "strnog" }) with
        {
            Models = new Dictionary<string, ModelProfile> { ["default"] = new(), ["strong"] = new() },
        };

        var error = Assert.Single(Validate(options));

        Assert.Equal(ValidationPhase.References, error.Phase);
        Assert.Equal("agents.extractor.model", error.Path);
        Assert.Equal("model profile \"strnog\" does not exist.", error.Problem);
        Assert.EndsWith("Did you mean \"strong\"?", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Profiles_must_name_existing_providers_and_fallbacks()
    {
        var options = new OfficinaOptions
        {
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new() { Fallbacks = ["default", "missing"] },
                ["other"] = new() { Provider = "claud" },
            },
        };

        var errors = Validate(options);

        Assert.Equal(["models.default.fallbacks[0]", "models.default.fallbacks[1]", "models.other.provider"], errors.Select(error => error.Path).Order(StringComparer.Ordinal));
        Assert.Contains(errors, error => error.Fix.EndsWith("Did you mean \"claude\"?", StringComparison.Ordinal));
    }

    [Fact]
    public void An_inline_profile_is_checked_like_a_named_one()
    {
        var error = Assert.Single(Validate(WithAgent(Extractor with { Model = new ModelProfile { Provider = "nobody" } })));

        Assert.Equal("agents.extractor.model.provider", error.Path);
    }

    [Fact]
    public void Provider_types_are_checked_when_the_host_names_them()
    {
        var context = new ValidationContext(new OfficinaOptions(), SettingsModel.Default) { ProviderTypes = new HashSet<string> { "openai" } };

        var error = Assert.Single(new ConfigurationValidator(SettingsModel.Default).Validate(context));

        Assert.Equal("providers.claude.type", error.Path);
    }

    [Fact]
    public void Every_error_is_reported_not_just_the_first()
    {
        var options = WithAgent(Extractor with { Instructions = "", Model = "missing" }) with
        {
            Run = new RunDefaults { Budget = new RunBudget { Cost = 0 } },
        };

        var errors = Validate(options);

        Assert.Equal(3, errors.Count);
        Assert.Equal([ValidationPhase.Shape, ValidationPhase.References, ValidationPhase.Invariants], errors.Select(error => error.Phase));
    }

    [Fact]
    public void Profiles_use_only_what_the_provider_declares()
    {
        var declared = new ProviderCapabilities { Efforts = ["low", "high"], Settings = ["temperature"], ToolChoices = ["auto"], MaxOutputTokens = 1000 };
        var options = new OfficinaOptions
        {
            Models = new Dictionary<string, ModelProfile>
            {
                ["default"] = new()
                {
                    Effort = "hihg",
                    ToolChoice = "none",
                    MaxOutputTokens = 2000,
                    Settings = new Dictionary<string, SettingValue> { ["temprature"] = 0.2, ["temperature"] = 0.2 },
                },
            },
        };

        var errors = Validate(options, _ => declared);

        Assert.All(errors, error => Assert.Equal(ValidationPhase.Provider, error.Phase));
        Assert.Equal(
            ["models.default.effort", "models.default.maxOutputTokens", "models.default.settings.temprature", "models.default.toolChoice"],
            errors.Select(error => error.Path).Order(StringComparer.Ordinal));
        Assert.Contains(errors, error => error.Fix.EndsWith("Did you mean \"high\"?", StringComparison.Ordinal));
    }

    [Fact]
    public void A_provider_that_declares_nothing_is_not_checked()
    {
        var options = new OfficinaOptions { Models = new Dictionary<string, ModelProfile> { ["default"] = new() { Effort = "anything" } } };

        Assert.Empty(Validate(options, _ => ProviderCapabilities.None));
    }

    [Theory]
    [InlineData("Use the key sk-ant-api03-abcdefghijklmnop to call the API.")]
    [InlineData("token ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    public void A_value_that_looks_like_a_credential_is_rejected(string text)
    {
        var error = Assert.Single(Validate(WithAgent(Extractor with { Instructions = text })));

        Assert.Equal(ValidationPhase.Shape, error.Phase);
        Assert.Equal("agents.extractor.instructions", error.Path);
        Assert.Contains("{ \"secret\": \"NAME\" }", error.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(text, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_literal_under_a_credential_name_is_rejected()
    {
        var options = new OfficinaOptions
        {
            Project = new ProjectOptions { Values = new Dictionary<string, string> { ["apiToken"] = "hunter2", ["maxTokens"] = "100" } },
            Models = new Dictionary<string, ModelProfile> { ["default"] = new() { Settings = new Dictionary<string, SettingValue> { ["password"] = "letmein" } } },
        };

        var errors = Validate(options);

        Assert.Equal(["models.default.settings.password", "project.values.apiToken"], errors.Select(error => error.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_budget_of_zero_or_less_weakens_INV_07(int cost)
    {
        var error = Assert.Single(Validate(new OfficinaOptions { Run = new RunDefaults { Budget = new RunBudget { Cost = cost } } }));

        Assert.Equal(ValidationPhase.Invariants, error.Phase);
        Assert.Equal("run.budget.cost", error.Path);
        Assert.Contains("INV-07", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void A_budget_removed_in_code_weakens_INV_07()
    {
        var error = Assert.Single(Validate(new OfficinaOptions { Run = new RunDefaults { Budget = null! } }));

        Assert.Equal((ValidationPhase.Invariants, "run.budget"), (error.Phase, error.Path));
    }

    [Fact]
    public void Errors_are_ordered_by_phase()
    {
        var options = WithAgent(Extractor with { Instructions = "Hello {{caller.id}}", Model = "missing" }) with
        {
            Run = new RunDefaults { Budget = new RunBudget { Time = TimeSpan.Zero } },
        };

        var phases = Validate(options).Select(error => error.Phase).ToArray();

        Assert.Equal([ValidationPhase.References, ValidationPhase.Prefix, ValidationPhase.Invariants], phases);
    }

    [Fact]
    public void An_error_reads_as_path_problem_and_fix()
    {
        var error = new ConfigurationError(ValidationPhase.References, "agents.developer.model", "model profile \"x\" does not exist.", "Add it to models.") { Location = "sof.json:4:7" };

        Assert.Equal("agents.developer.model: model profile \"x\" does not exist. Add it to models. (sof.json:4:7)", error.ToString());
    }

    private static OfficinaOptions WithAgent(AgentDefinition agent) =>
        new() { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = agent } };

    private static IReadOnlyList<ConfigurationError> Validate(OfficinaOptions options, Func<string, ProviderCapabilities?>? describe = null) =>
        new ConfigurationValidator(SettingsModel.Default).Validate(new ValidationContext(options, SettingsModel.Default) { DescribeProvider = describe ?? (_ => null) });
}
