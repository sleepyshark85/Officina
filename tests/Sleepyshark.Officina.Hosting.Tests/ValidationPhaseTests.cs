using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Configuration.Validation;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Hosting.Configuration;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>
/// One test or more per validation phase of configuration reference §14 that applies to the settings so
/// far (CFG-06, TEST-04). Every error names the setting, the problem and the fix, and where it was written.
/// </summary>
public sealed class ValidationPhaseTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("""{ "agents": { """, "the file is not valid JSON")]
    [InlineData("""[1, 2]""", "the file is not a JSON object.")]
    [InlineData("""{ "a": 1 } { }""", "the file is not valid JSON")]
    [InlineData("""{ "project": {}, "project": {} }""", "\"project\" appears twice in the same object.")]
    [InlineData("""{ "formatVersion": 2 }""", "format version 2 is not supported.")]
    public void Phase_1_parse_rejects_invalid_JSON_and_unknown_format_versions(string text, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(ValidationPhase.Parse, error.Phase);
        Assert.StartsWith(problem, error.Problem, StringComparison.Ordinal);
        Assert.StartsWith("sof.json:1:", error.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1_gives_the_line_and_column_of_a_syntax_error()
    {
        folder.Write("sof.json", "{\n  \"agents\": {\n    \"a\": { \"instructions\": \"x\" }\n    \"b\": {}\n  }\n}");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal("sof.json:4:5", error.Location);
        Assert.Contains("Comments (//, /* */) and trailing commas are allowed.", error.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_2_shape_rejects_unknown_settings_with_a_suggestion_and_their_position()
    {
        folder.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "x", "instruction": "y", "modle": "default" } },
              "runn": {}
            }
            """);

        var errors = folder.Load().Errors;

        Assert.Equal(
            [
                ("agents.a.instruction", "sof.json:2:58", "Remove it, or check the spelling. Did you mean \"instructions\"?"),
                ("agents.a.modle", "sof.json:2:72", "Remove it, or check the spelling. Did you mean \"model\"?"),
                ("runn", "sof.json:3:11", "Remove it, or check the spelling. Did you mean \"run\"?"),
            ],
            errors.Select(error => (error.Path, error.Location, error.Fix)));
        Assert.All(errors, error => Assert.Equal(ValidationPhase.Shape, error.Phase));
    }

    [Theory]
    [InlineData("""{ "run": { "permissionMode": "sometimes" } }""", "run.permissionMode", "is text, but must be one of \"ask\", \"auto\", \"readOnly\".")]
    [InlineData("""{ "run": { "budget": { "time": "8 hours" } } }""", "run.budget.time", "is text, but must be a duration: a number with a unit, ms, s, m, h or d.")]
    [InlineData("""{ "run": { "budget": { "cost": "a lot" } } }""", "run.budget.cost", "is text, but must be a number.")]
    [InlineData("""{ "models": { "default": { "maxOutputTokens": 1.5 } } }""", "models.default.maxOutputTokens", "is 1.5, but must be a whole number.")]
    [InlineData("""{ "agents": { "a": { "instructions": ["x"] } } }""", "agents.a.instructions", "is a list, but must be text, or { \"file\": \"path\" }.")]
    [InlineData("""{ "models": { "default": { "fallbacks": "fast" } } }""", "models.default.fallbacks", "is text, but must be a list.")]
    [InlineData("""{ "agents": { "my agent": { "instructions": "x" } } }""", "agents[\"my agent\"]", "\"my agent\" is not a valid name.")]
    [InlineData("""{ "models": { "default": { "fallbacks": [null] } } }""", "models.default.fallbacks[0]", "a list item cannot be null.")]
    [InlineData("""{ "capabilities": { "sandbox": true } }""", "capabilities.sandbox", "capability \"sandbox\" is not available in this application.")]
    public void Phase_2_shape_rejects_values_of_the_wrong_type(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, path, problem), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void Phase_2_rejects_a_secret_written_as_plain_text_without_repeating_it()
    {
        const string key = "sk-ant-api03-0123456789abcdefghij";
        folder.Write("sof.json", $$"""{ "providers": { "claude": { "apiKey": "{{key}}" } } }""");

        var errors = folder.Load().Errors;

        var error = Assert.Single(errors);
        Assert.Equal(("providers.claude.apiKey", "is plain text, but a secret is never written in configuration."), (error.Path, error.Problem));
        Assert.Contains("{ \"secret\": \"NAME\" }", error.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_2_rejects_a_credential_like_value_in_any_setting()
    {
        folder.Write("sof.json", """{ "project": { "values": { "deployKey": "ghp_abcdefghijklmnopqrstuvwxyz0123456789" } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, "project.values.deployKey", "looks like a credential."), (error.Phase, error.Path, error.Problem));
        Assert.Equal("sof.json:1:41", error.Location);
    }

    [Fact]
    public void Phase_2_checks_environment_variables_and_run_options_too()
    {
        var configuration = folder.Load(
            variables: new() { ["SOF__run__permissionMode"] = "never", ["SOF__run____x"] = "1" },
            runOptions: new RunOption("run.budget.costs", "3", "--set run.budget.costs"));

        Assert.Equal(
            [("", "SOF__run____x"), ("run.budget.costs", "--set run.budget.costs"), ("run.permissionMode", "SOF__run__permissionMode")],
            configuration.Errors.Select(error => (error.Path, error.Location!)).Order());
    }

    [Theory]
    [InlineData("""{ "extends": ["preset:coding-teem"] }""", "extends[0]", "preset \"coding-teem\" does not exist.")]
    [InlineData("""{ "extends": ["missing.json"] }""", "extends[0]", "file \"missing.json\" does not exist.")]
    [InlineData("""{ "extends": ["sof.json"] }""", "extends[0]", "extends forms a cycle: sof.json → sof.json.")]
    [InlineData("""{ "agents": { "a": { "extends": "b", "instructions": "x" }, "b": { "extends": "a" } } }""", "agents.b.extends", "agent definitions a → b → a extend each other in a cycle.")]
    [InlineData("""{ "agents": { "a": { "extends": "bse", "instructions": "x" }, "base": { "instructions": "y" } } }""", "agents.a.extends", "agent definition \"bse\" does not exist.")]
    public void Phase_3_merge_rejects_cycles_and_missing_bases(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Merge, path, problem), (error.Phase, error.Path, error.Problem));
        Assert.NotNull(error.Location);
    }

    [Fact]
    public void Phase_3_names_every_file_of_a_cycle()
    {
        folder.Write("a.json", """{ "extends": ["b.json"] }""").Write("b.json", """{ "extends": ["a.json"] }""").Write("sof.json", """{ "extends": ["a.json"] }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal("extends forms a cycle: a.json → b.json → a.json.", error.Problem);
        Assert.Equal("b.json:1:15", error.Location);
    }

    [Fact]
    public void Phase_4_references_rejects_missing_names_with_the_position_of_the_reference()
    {
        using var withTypes = new ConfigurationFolder { ProviderTypes = new Dictionary<string, ProviderCapabilities> { ["claude"] = ProviderCapabilities.None } };
        withTypes.Write("sof.json", """
            {
              "models": { "strong": { "provider": "anthropic", "fallbacks": ["backup"] } },
              "agents": { "a": { "instructions": "Run {{project.values.test}}.", "model": "strnog" } },
              "providers": { "claude": { "type": "claud" } }
            }
            """);

        var errors = withTypes.Load().Errors;

        Assert.All(errors, error => Assert.Equal(ValidationPhase.References, error.Phase));
        Assert.Equal(
            [
                ("providers.claude.type", "sof.json:4:38"),
                ("models.strong.provider", "sof.json:2:39"),
                ("models.strong.fallbacks[0]", "sof.json:2:66"),
                ("agents.a.model", "sof.json:3:79"),
                ("agents.a.instructions", "sof.json:3:38"),
            ],
            errors.Select(error => (error.Path, error.Location)));
    }

    [Fact]
    public void Phase_4_rejects_an_included_file_that_does_not_exist()
    {
        folder.Write("sof.json", """{ "agents": { "a": { "instructions": { "file": "prompts/a.md" } } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.References, "agents.a.instructions.file", "file \"prompts/a.md\" does not exist."), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void Phase_5_capabilities_rejects_unmet_dependencies_and_capabilities_not_enabled()
    {
        using var withCapabilities = new ConfigurationFolder { Capabilities = new CapabilityRegistry([new FakeCapability("team", "taskBoard"), new FakeCapability("taskBoard")]) };
        withCapabilities.Write("sof.json", """
            {
              "capabilities": { "team": { "enabled": true, "endpoint": "https://team" } },
              "agents": { "a": { "instructions": "x", "capabilities": ["team", "taskBoard"] } }
            }
            """);

        var errors = withCapabilities.Load().Errors;

        Assert.Equal(
            [("capabilities.team", "team needs taskBoard, which is off."), ("agents.a.capabilities[1]", "the agent uses taskBoard, which the application has not enabled.")],
            errors.Select(error => (error.Path, error.Problem)));
        Assert.All(errors, error => Assert.Equal(ValidationPhase.Capabilities, error.Phase));
        Assert.Equal("sof.json:2:29", errors[0].Location);
    }

    [Fact]
    public void Phase_6_provider_rejects_what_the_provider_does_not_declare()
    {
        using var withTypes = new ConfigurationFolder
        {
            ProviderTypes = new Dictionary<string, ProviderCapabilities> { ["claude"] = new() { Efforts = ["low", "medium", "high"], Settings = ["thinkingDisplay"] } },
        };
        withTypes.Write("sof.json", """{ "models": { "default": { "effort": "max", "settings": { "temperature": 0.5 } } } }""");

        var errors = withTypes.Load().Errors;

        Assert.Equal(["models.default.effort", "models.default.settings.temperature"], errors.Select(error => error.Path));
        Assert.All(errors, error => Assert.Equal(ValidationPhase.Provider, error.Phase));
    }

    [Fact]
    public void Phase_7_tools_and_later_phases_run_in_order_with_capability_rules()
    {
        // No tools exist before S03; a capability's rule in the tools phase shows where those rules run.
        using var withCapability = new ConfigurationFolder { Capabilities = new CapabilityRegistry([new FakeCapability()]) };
        withCapability.Write("sof.json", """
            {
              "capabilities": { "fake": { "enabled": true, "endpoint": "e", "size": 13 } },
              "agents": { "a": { "instructions": "Today is {{now:date}}.", "model": "missing" } }
            }
            """);

        var phases = withCapability.Load().Errors.Select(error => error.Phase);

        Assert.Equal([ValidationPhase.References, ValidationPhase.Tools, ValidationPhase.Prefix], phases);
    }

    [Fact]
    public void Phase_9_prefix_rejects_volatile_placeholders_in_instructions()
    {
        folder.Write("sof.json", """{ "agents": { "lead": { "instructions": "Greet {{caller.id}}." } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(
            "sof.json:1:41: agents.lead.instructions: placeholder {{caller.id}} is not allowed in the stable prefix. Move it to context.operatingFacts (CTX-02, CFG-14).",
            $"{error.Location}: {error with { Location = null }}");
    }

    [Fact]
    public void Every_error_is_reported_at_once_without_repeats_for_the_same_setting()
    {
        folder.Write("sof.json", """
            {
              "formatVersion": 1,
              "agents": { "a": { "instructions": 42 }, "b": { "model": "nope" } },
              "run": { "budget": { "cost": -1 }, "permisionMode": "ask" }
            }
            """);

        var errors = folder.Load().Errors;

        // agents.a.instructions has the wrong type, so it is not also reported as missing.
        Assert.Equal(
            ["agents.a.instructions", "run.permisionMode", "agents.b.instructions", "agents.b.model", "run.budget.cost"],
            errors.Select(error => error.Path));
    }
}
