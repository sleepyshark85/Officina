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

    // INV-07, LOOP-05.
    [Fact]
    public void A_turn_needs_a_budget_above_zero_and_a_way_to_complete()
    {
        var options = WithAgent(Extractor with
        {
            Budget = new() { Turn = new() { Tokens = 0 } },
            StopWhen = new() { Finished = false },
            Stall = new() { IterationsWithoutProgress = 0 },
        });

        Assert.Equal(
            [(ValidationPhase.Shape, "agents.extractor.stall.iterationsWithoutProgress"), (ValidationPhase.Shape, "agents.extractor.stopWhen"),
                (ValidationPhase.Invariants, "agents.extractor.budget.turn.tokens")],
            options.Validate().Select(error => (error.Phase, error.Path)));
        Assert.Equal(
            "tool \"submit\" does not exist.",
            Assert.Single(WithAgent(Extractor with { StopWhen = new() { FinishTool = "submit" } }).Validate()).Problem);
    }

    [Fact]
    public void A_range_is_checked_wherever_the_setting_is()
    {
        var options = new OfficinaOptions { Models = new Dictionary<string, ModelProfile> { ["default"] = new() { MaxOutputTokens = 0 } } };

        var error = Assert.Single(options.Validate());

        Assert.Equal(("models.default.maxOutputTokens", "must be at least 1."), (error.Path, error.Problem));
    }

    [Fact]
    public void A_protected_path_needs_a_path()
    {
        var workspace = new WorkspaceOptions { Enabled = true, ProtectedPaths = [new() { Path = null! }] };

        var error = Assert.Single(new OfficinaOptions { Capabilities = new() { Workspace = workspace } }.Validate());

        Assert.Equal(("capabilities.workspace.protectedPaths[0].path", "is required but not set. Add it; it has no default."), (error.Path, error.Problem));
    }

    [Fact]
    public void A_command_rule_needs_a_pattern_and_secrets_go_to_agents_that_exist()
    {
        var sandbox = new SandboxOptions
        {
            Enabled = true,
            CommandRules = [new() { Match = null! }],
            Secrets = new Dictionary<string, IReadOnlyList<string>> { ["develper"] = ["NUGET_TOKEN"] },
        };

        var options = WithAgent(Extractor) with { Capabilities = new() { Sandbox = sandbox, Workspace = new() { Enabled = true } } };

        Assert.Equal(
            [("capabilities.sandbox.commandRules[0].match", "is required but not set. Add it; it has no default."), ("capabilities.sandbox.secrets", "agent \"develper\" does not exist.")],
            options.Validate().Select(error => (error.Path, error.Problem)));
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

    [Fact]
    public void Tool_servers_and_knowledge_sources_are_checked_with_what_refers_to_them()
    {
        var options = WithAgent(Extractor with { Context = new() { Retrieval = new() { BeforeTurn = ["wiki"] } } }) with
        {
            ToolServers = new Dictionary<string, ToolServerOptions>
            {
                ["local"] = new(),
                ["remote"] = new() { Transport = ToolServerTransport.Http, Url = "tracker/mcp" },
            },
            Knowledge = new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "builtin:index" } },
            Capabilities = new() { Knowledge = new() { Enabled = true } },
            Tools = new Dictionary<string, ToolOptions>
            {
                ["a"] = new() { Source = "mcp:github/create_issue" },
                ["b"] = new() { Source = "mcp:local" },
                ["c"] = new() { Source = "knowledge:wiki" },
            },
        };

        Assert.Equal(
            [
                "toolServers.local.command", "toolServers.remote.url", "knowledge.handbook.use", "tools.b.source",
                "agents.extractor.context.retrieval.beforeTurn", "tools.a.source", "tools.c.source",
            ],
            options.Validate().Select(error => error.Path));
    }

    // CAP-01, CAP-03, TEST-04.
    [Fact]
    public void A_capability_in_use_must_be_on()
    {
        var options = WithAgent(Extractor with { Context = new() { History = new() { Strategy = HistoryStrategy.Full } } }) with
        {
            Knowledge = new Dictionary<string, KnowledgeOptions> { ["handbook"] = new() { Use = "extension:Handbook" } },
        };

        Assert.Equal(
            [
                ("knowledge", "needs the knowledge capability, which is off.", "Set capabilities.knowledge.enabled to true."),
                ("agents.extractor.context.history.strategy", "needs the conversationStore capability, which is off.", "Set capabilities.conversationStore.enabled to true."),
            ],
            options.Validate().Select(error => (error.Path, error.Problem, error.Fix)));
    }

    // CAP-03, TEST-04.
    [Fact]
    public void A_capability_that_is_on_needs_the_ones_it_requires()
    {
        var error = Assert.Single((WithAgent(Extractor) with { Capabilities = new() { Sandbox = new() { Enabled = true } } }).Validate());

        Assert.Equal(
            (ValidationPhase.Capabilities, "capabilities.sandbox.enabled", "sandbox needs the workspace capability, which is off.", "Set capabilities.workspace.enabled to true."),
            (error.Phase, error.Path, error.Problem, error.Fix));
    }

    // CAP-02, TEST-08: its tools and storage are in the history tests.
    [Fact]
    public void The_settings_of_a_capability_that_is_off_are_not_required()
    {
        var sandbox = new SandboxOptions { CommandRules = [new() { Match = null! }] };

        Assert.Empty((WithAgent(Extractor) with { Capabilities = new() { Sandbox = sandbox } }).Validate());
    }

    [Fact]
    public void History_is_shortened_by_the_provider_or_an_extension()
    {
        var error = Assert.Single(WithAgent(Extractor with { Context = new() { History = new() { Shortening = "summarise" } } }).Validate());

        Assert.Equal(("agents.extractor.context.history.shortening", "\"summarise\" is not a way to shorten history."), (error.Path, error.Problem));
    }

    private static OfficinaOptions WithAgent(AgentDefinition agent) =>
        new() { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = agent } };
}
