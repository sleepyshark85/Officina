using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>
/// A test per validation phase of configuration reference §14 that applies to the settings so far (CFG-06). Every
/// error names the setting, the problem and the fix, and the file, variable or option that set it.
/// </summary>
public sealed class ValidationPhaseTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("""{ "agents": { """)]
    [InlineData("""[1, 2]""")]
    [InlineData("""{ "project": { "name": "a" }, "project": { "name": "b" } }""")]
    public void Phase_1_parse_rejects_a_file_that_is_not_a_JSON_object(string text)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Parse, "sof.json"), (error.Phase, error.Location));
        Assert.StartsWith("the file cannot be read: ", error.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1_parse_rejects_an_unknown_format_version()
    {
        folder.Write("sof.json", """{ "formatVersion": 2 }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Parse, "formatVersion", "format version 2 is not supported."), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void Phase_2_shape_reports_every_unknown_setting_with_its_file()
    {
        folder.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "x", "instruction": "y", "modle": "default" } },
              "capabilities": { "sandbox": true },
              "extends": ["base.json"]
            }
            """);

        var errors = folder.Load().Errors;

        Assert.Equal(
            [
                ("agents.a.instruction", "\"instruction\" is not a setting here."),
                ("agents.a.modle", "\"modle\" is not a setting here."),
                ("capabilities", "\"capabilities\" is not a setting here."),
                ("extends", "\"extends\" is not a setting here."),
            ],
            errors.Select(error => (error.Path, error.Problem)).Order());
        Assert.All(errors, error => Assert.Equal((ValidationPhase.Shape, "sof.json"), (error.Phase, error.Location)));
    }

    [Fact]
    public void Phase_2_checks_environment_variables_and_run_options_too()
    {
        var configuration = folder.Load(variables: new() { ["SOF__run__costs"] = "1" }, runOptions: new RunOption("run.budget.costs", "3", "--budget"));

        Assert.Equal(
            [("run.budget.costs", "--budget"), ("run.costs", "SOF__run__costs")],
            configuration.Errors.Select(error => (error.Path, error.Location!)).Order());
    }

    [Theory]
    [InlineData("""{ "run": { "permissionMode": "sometimes" } }""", "Run:PermissionMode")]
    [InlineData("""{ "models": { "default": { "toolChoice": "banana" } } }""", "models.default toolChoice")]
    [InlineData("""{ "run": { "budget": { "time": "8 hours" } } }""", "Run:Budget:Time")]
    [InlineData("""{ "run": { "budget": { "cost": "a lot" } } }""", "Run:Budget:Cost")]
    [InlineData("""{ "models": { "default": { "maxOutputTokens": 1.5 } } }""", "models.default maxOutputTokens")]
    public void Phase_2_reports_a_value_of_the_wrong_type_with_the_binders_message(string text, string setting)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(ValidationPhase.Shape, error.Phase);
        Assert.All(setting.Split(' '), part => Assert.Contains(part, $"{error.Path} {error.Problem}", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("""{ "agents": { "a": { "model": "default" } } }""", "agents.a.instructions", "is required but not set. Add it; it has no default.")]
    [InlineData("""{ "models": { "default": { "model": null } } }""", "models.default.model", "is required but not set. Add it; it has no default.")]
    [InlineData("""{ "agents": { "my.agent": { "instructions": "x" } } }""", "agents.my.agent", "\"my.agent\" is not a valid name.")]
    [InlineData("""{ "agents": { "a": { "instructions": ["x"] } } }""", "agents.a.instructions", "is a list or a section, but it is a single value.")]
    public void Phase_2_rejects_missing_and_empty_values(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, path, problem, "sof.json"), (error.Phase, error.Path, error.Problem, error.Location));
    }

    [Fact]
    public void Phase_2_rejects_a_secret_written_as_plain_text()
    {
        const string key = "sk-ant-api03-0123456789abcdefghij";
        folder.Write("sof.json", $$"""{ "providers": { "claude": { "apiKey": "{{key}}" } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, "providers.claude.apiKey"), (error.Phase, error.Path));
        Assert.StartsWith("Write { \"secret\": \"NAME\" }", error.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "agents": { "a": { "extends": "b", "instructions": "x" }, "b": { "extends": "a" } } }""", "agents.b.extends", "agent definitions a → b → a extend each other in a cycle.")]
    [InlineData("""{ "agents": { "a": { "extends": "bse", "instructions": "x" }, "base": { "instructions": "y" } } }""", "agents.a.extends", "agent definition \"bse\" does not exist.")]
    public void Phase_3_merge_rejects_cycles_and_missing_bases(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = folder.Load().Errors.First(error => error.Phase == ValidationPhase.Merge);

        Assert.Equal((path, problem, "sof.json"), (error.Path, error.Problem, error.Location));
    }

    [Fact]
    public void Phase_4_references_rejects_missing_names_with_where_they_were_written()
    {
        folder.Write("sof.json", """{ "models": { "strong": { "provider": "anthropic" } } }""")
            .Write("sof.ci.json", """{ "agents": { "a": { "instructions": "Run {{project.values.test}}.", "model": "strnog" } } }""");

        var errors = folder.Load("ci").Errors;

        Assert.All(errors, error => Assert.Equal(ValidationPhase.References, error.Phase));
        Assert.Equal(
            [("agents.a.instructions", "sof.ci.json"), ("agents.a.model", "sof.ci.json"), ("models.strong.provider", "sof.json")],
            errors.Select(error => (error.Path, error.Location!)).Order());
    }

    [Fact]
    public void Phase_9_prefix_rejects_caller_work_and_time_placeholders_in_instructions()
    {
        folder.Write("sof.json", """{ "agents": { "lead": { "instructions": "Greet {{caller.id}}." } } }""");

        Assert.Equal(
            "sof.json: agents.lead.instructions: placeholder {{caller.id}} is not allowed in instructions. "
                + "Instructions are the same for every call, so they cannot use caller, work or time values.",
            Assert.Single(folder.Load().Errors).ToString());
    }

    [Fact]
    public void Every_unknown_setting_and_rule_error_is_reported_at_once()
    {
        folder.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "" }, "b": { "model": "nope", "instructions": "x" } },
              "run": { "budget": { "cost": -1 }, "permisionMode": "ask" }
            }
            """);

        Assert.Equal(
            ["agents.a.instructions", "agents.b.model", "run.budget.cost", "run.permisionMode"],
            folder.Load().Errors.Select(error => error.Path).Order());
    }
}
