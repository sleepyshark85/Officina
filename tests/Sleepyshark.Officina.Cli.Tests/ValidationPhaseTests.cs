using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Cli.Tests;

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

        Assert.Equal(ValidationPhase.Parse, error.Phase);
        Assert.StartsWith("the configuration cannot be read: ", error.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1_parse_requires_sof_json()
    {
        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(ValidationPhase.Parse, error.Phase);
        Assert.Contains("sof.json", error.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase_1_parse_rejects_an_unknown_format_version()
    {
        folder.Write("sof.json", """{ "formatVersion": 2 }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Parse, "formatVersion", "format version 2 is not supported.", "sof.json"), (error.Phase, error.Path, error.Problem, error.Location));
    }

    [Fact]
    public void Phase_2_rejects_a_secret_written_as_plain_text_without_repeating_it()
    {
        const string key = "sk-ant-api03-0123456789abcdefghij";
        folder.Write("sof.json", $$"""{ "providers": { "claude": { "apiKey": "{{key}}" } } }""");

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, "providers.claude.apiKey"), (error.Phase, error.Path));
        Assert.StartsWith("Write { \"secret\": \"NAME\" }", error.Fix, StringComparison.Ordinal);
        Assert.DoesNotContain(key, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "run": { "permissionMode": "sometimes" } }""", "Run:PermissionMode")]
    [InlineData("""{ "run": { "budget": { "time": "8 hours" } } }""", "Run:Budget:Time")]
    [InlineData("""{ "run": { "budget": { "cost": "a lot" } } }""", "Run:Budget:Cost")]
    public void Phase_2_reports_a_value_of_the_wrong_type_with_the_binders_message(string text, string setting)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal(ValidationPhase.Shape, error.Phase);
        Assert.Contains(setting, error.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "agents": { "a": { "model": "default" } } }""", "agents.a.instructions", "is required but not set. Add it; it has no default.", null)]
    [InlineData("""{ "models": { "default": { "model": null } } }""", "models.default.model", "is required but not set. Add it; it has no default.", "sof.json")]
    [InlineData("""{ "agents": { "my.agent": { "instructions": "x" } } }""", "agents.my.agent", "\"my.agent\" is not a valid name.", null)]
    public void Phase_2_rejects_missing_and_empty_values(string text, string path, string problem, string? location)
    {
        folder.Write("sof.json", text);

        var error = Assert.Single(folder.Load().Errors);

        Assert.Equal((ValidationPhase.Shape, path, problem, location), (error.Phase, error.Path, error.Problem, error.Location));
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
    public void Every_rule_error_is_reported_at_once()
    {
        folder.Write("sof.json", """
            {
              "agents": { "a": { "instructions": "" }, "b": { "model": "nope", "instructions": "x" } },
              "run": { "budget": { "cost": -1 } }
            }
            """);

        Assert.Equal(["agents.a.instructions", "agents.b.model", "run.budget.cost"], folder.Load().Errors.Select(error => error.Path).Order());
    }
}
