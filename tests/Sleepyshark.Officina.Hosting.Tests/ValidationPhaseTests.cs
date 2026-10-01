using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Hosting.Configuration;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>
/// A test per validation phase of configuration reference §14 that applies to the settings so far (CFG-06). Every
/// error names the setting, the problem and the fix, and where it was written.
/// </summary>
public sealed class ValidationPhaseTests : IDisposable
{
    private readonly ConfigurationFolder folder = new();

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData("""{ "agents": { """, "the file is not valid JSON.")]
    [InlineData("""[1, 2]""", "the file is not a JSON object.")]
    [InlineData("""{ "a": 1 } { }""", "the file is not valid JSON.")]
    [InlineData("""{ "project": {}, "project": {} }""", "\"project\" appears twice in the same object.")]
    [InlineData("""{ "formatVersion": 2 }""", "format version 2 is not supported.")]
    public void Phase_1_parse_rejects_invalid_JSON_and_unknown_format_versions(string text, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Parse, problem), (error.Phase, error.Problem));
        Assert.StartsWith("sof.json:1:", error.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1_gives_the_line_and_column_of_a_syntax_error()
    {
        folder.Write("sof.json", "{\n  \"agents\": {\n    \"a\": { \"instructions\": \"x\" }\n    \"b\": {}\n  }\n}");

        Assert.Equal("sof.json:4:5", Assert.Single(folder.Load().Errors).Location);
    }

    [Fact]
    public void Phase_2_shape_reports_every_unknown_setting_with_its_position()
    {
        folder.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "x", "instruction": "y", "modle": "default" } },
              "capabilities": { "sandbox": true }
            }
            """);

        var errors = folder.Load().Errors;

        Assert.Equal(
            [
                ("agents.a.instruction", "sof.json:2:58", "\"instruction\" is not a setting here."),
                ("agents.a.modle", "sof.json:2:72", "\"modle\" is not a setting here."),
                ("capabilities", "sof.json:3:19", "\"capabilities\" is not a setting here."),
            ],
            errors.Select(error => (error.Path, error.Location, error.Problem)).Order());
        Assert.All(errors, error => Assert.Equal(ValidationPhase.Shape, error.Phase));
    }

    [Theory]
    [InlineData(
        """{ "run": { "permissionMode": "sometimes" } }""",
        "run.permissionMode",
        "is text, but must be one of \"ask\", \"auto\", \"readOnly\".")]
    [InlineData(
        """{ "models": { "default": { "toolChoice": "banana" } } }""",
        "models.default.toolChoice",
        "is text, but must be one of \"auto\", \"none\".")]
    [InlineData(
        """{ "run": { "budget": { "time": "8 hours" } } }""",
        "run.budget.time",
        "is text, but must be a duration: a number with a unit, ms, s, m, h or d, such as \"30m\".")]
    [InlineData(
        """{ "run": { "budget": { "time": "99999999999d" } } }""",
        "run.budget.time",
        "is text, but must be a duration: a number with a unit, ms, s, m, h or d, such as \"30m\".")]
    [InlineData("""{ "run": { "budget": { "cost": "a lot" } } }""", "run.budget.cost", "is text, but must be a number.")]
    [InlineData(
        """{ "models": { "default": { "maxOutputTokens": 1.5 } } }""",
        "models.default.maxOutputTokens",
        "is a number, but must be a whole number.")]
    [InlineData("""{ "agents": { "a": { "instructions": ["x"] } } }""", "agents.a.instructions", "is a list, but must be text.")]
    [InlineData("""{ "agents": { "a": { "model": "default" } } }""", "agents.a.instructions", "is required but not set.")]
    [InlineData("""{ "providers": { "x": { "apiKey": {} } } }""", "providers.x.apiKey.secret", "is required but not set.")]
    public void Phase_2_shape_rejects_values_of_the_wrong_type(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, path, problem), (error.Phase, error.Path, error.Problem));
        Assert.StartsWith("sof.json:1:", error.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_2_rejects_a_secret_written_as_plain_text_without_repeating_it()
    {
        const string key = "sk-ant-api03-0123456789abcdefghij";
        folder.Write("sof.json", $$"""{ "providers": { "claude": { "apiKey": "{{key}}" } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(("providers.claude.apiKey", "is not a secret reference."), (error.Path, error.Problem));
        Assert.Contains("{ \"secret\": \"NAME\" }", error.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_2_checks_environment_variables_and_run_options_too()
    {
        var configuration = folder.Load(
            variables: new() { ["SOF__run__permissionMode"] = "never", ["SOF__run____x"] = "1", ["SOF__RUN__BUDGET__COST"] = "8" },
            runOptions: new RunOption("run.budget.costs", "3", "--budget"));

        Assert.Equal(
            [
                ("", "SOF__run____x"),
                ("RUN", "SOF__RUN__BUDGET__COST"),
                ("run.budget.costs", "--budget"),
                ("run.permissionMode", "SOF__run__permissionMode"),
            ],
            configuration.Errors.Select(error => (error.Path, error.Location!)).Order());
        var uppercase = configuration.Errors.Single(error => error.Path == "RUN");
        Assert.EndsWith("Setting names are case-sensitive, as in SOF__run__permissionMode.", uppercase.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_2_rejects_a_duplicate_key_in_a_variable_value()
    {
        var error = Assert.Single(folder.Load(variables: new() { ["SOF__project"] = """{"name":"a","name":"b"}""" }).Errors);

        Assert.Equal((ValidationPhase.Shape, "project", "has a key twice in the same object."), (error.Phase, error.Path, error.Problem));
    }

    [Fact]
    public void A_null_in_a_list_is_an_error_not_a_crash()
    {
        folder.Write("sof.json", """{ "extends": [null], "models": { "default": { "settings": { "stop": [null] } } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(("extends[0]", "is not a file path."), (error.Path, error.Problem));
    }

    [Theory]
    [InlineData("""{ "extends": ["missing.json"] }""", "extends", "file \"missing.json\" does not exist.")]
    [InlineData("""{ "extends": ["sof.json"] }""", "extends", "extends forms a cycle: sof.json → sof.json.")]
    [InlineData("""{ "extends": ["preset:coding-team"] }""", "extends[0]", "is not a file path.")]
    [InlineData(
        """{ "agents": { "a": { "extends": "b", "instructions": "x" }, "b": { "extends": "a" } } }""",
        "agents.b.extends",
        "agent definitions a → b → a extend each other in a cycle.")]
    [InlineData(
        """{ "agents": { "a": { "extends": "bse", "instructions": "x" }, "base": { "instructions": "y" } } }""",
        "agents.a.extends",
        "agent definition \"bse\" does not exist.")]
    public void Phase_3_merge_rejects_cycles_and_missing_bases(string text, string path, string problem)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Merge, path, problem), (error.Phase, error.Path, error.Problem));
        Assert.NotNull(error.Location);
    }

    [Fact]
    public void Phase_4_references_rejects_missing_names_with_the_position_of_the_reference()
    {
        folder.Write("sof.json", """
            {
              "models": { "strong": { "provider": "anthropic" } },
              "agents": { "a": { "instructions": "Run {{project.values.test}}.", "model": "strnog" } }
            }
            """);

        var errors = folder.Load().Errors;

        Assert.All(errors, error => Assert.Equal(ValidationPhase.References, error.Phase));
        Assert.Equal(
            [("agents.a.instructions", "sof.json:3:38"), ("agents.a.model", "sof.json:3:79"), ("models.strong.provider", "sof.json:2:39")],
            errors.Select(error => (error.Path, error.Location!)).Order());
    }

    [Fact]
    public void Phase_9_prefix_rejects_caller_work_and_time_placeholders_in_instructions()
    {
        folder.Write("sof.json", """{ "agents": { "lead": { "instructions": "Greet {{caller.id}}." } } }""");

        Assert.Equal(
            "sof.json:1:41: agents.lead.instructions: placeholder {{caller.id}} is not allowed in instructions. "
                + "Instructions are the same for every call, so they cannot use caller, work or time values.",
            Assert.Single(folder.Load().Errors).ToString());
    }

    [Fact]
    public void Every_error_is_reported_at_once_without_repeats_for_the_same_setting()
    {
        folder.Write("sof.json", """
            {
              "agents": { "a": { "instructions": 42 }, "b": { "model": "nope", "instructions": "x" } },
              "run": { "budget": { "cost": -1 }, "permisionMode": "ask" }
            }
            """);

        // agents.a.instructions has the wrong type, so it is not also reported as missing.
        Assert.Equal(
            ["agents.a.instructions", "agents.b.model", "run.budget.cost", "run.permisionMode"],
            folder.Load().Errors.Select(error => error.Path).Order());
    }
}
