using System.Text.Json;
using Sleepyshark.Officina.Core.Conditions;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Core.Tests.Conditions;

/// <summary>The condition language (CFG-13): parsing, evaluation and checking against schemas.</summary>
public class ConditionTests
{
    private const string Values = """
        {
          "output": { "severity": "high", "score": 0.85, "count": 3, "tags": ["bug", "ui"], "ticket": null,
                      "items": [ { "name": "a" }, { "name": "b" } ] },
          "checks": { "tests": { "passed": true, "findings": 0 } },
          "outcome": "completed"
        }
        """;

    [Theory]
    [InlineData("""{ "field": "output.severity", "equals": "high" }""", true)]
    [InlineData("""{ "field": "output.severity", "equals": "low" }""", false)]
    [InlineData("""{ "field": "output.severity", "in": ["bug", "high"] }""", true)]
    [InlineData("""{ "field": "output.score", "gte": 0.8 }""", true)]
    [InlineData("""{ "field": "output.score", "gt": 0.85 }""", false)]
    [InlineData("""{ "field": "output.score", "lt": 1 }""", true)]
    [InlineData("""{ "field": "output.count", "lte": 3 }""", true)]
    [InlineData("""{ "field": "output.count", "equals": 3.0 }""", true)]
    [InlineData("""{ "field": "checks.tests.passed", "equals": true }""", true)]
    [InlineData("""{ "field": "output.items[1].name", "equals": "b" }""", true)]
    [InlineData("""{ "field": "output.items[5].name", "equals": "b" }""", false)]
    [InlineData("""{ "field": "output.ticket", "exists": true }""", false)]
    [InlineData("""{ "field": "output.ticket", "exists": false }""", true)]
    [InlineData("""{ "field": "output.missing", "exists": false }""", true)]
    [InlineData("""{ "field": "output.severity", "gt": 1 }""", false)]
    [InlineData("""{ "all": [ { "field": "outcome", "equals": "completed" }, { "field": "output.count", "gt": 2 } ] }""", true)]
    [InlineData("""{ "any": [ { "field": "outcome", "equals": "failed" }, { "field": "output.count", "gt": 5 } ] }""", false)]
    [InlineData("""{ "not": { "field": "outcome", "equals": "failed" } }""", true)]
    public void Conditions_evaluate_over_structured_values(string json, bool expected)
    {
        var condition = Parse(json);

        using var values = JsonDocument.Parse(Values);
        Assert.Equal(expected, condition.Evaluate(values.RootElement));
    }

    [Fact]
    public void A_condition_is_written_back_in_its_file_form()
    {
        const string json = """{"all":[{"field":"output.items[0].name","equals":"a"},{"not":{"field":"output.score","lt":0.5}}]}""";

        Assert.Equal(json, Parse(json).ToJson().ToJsonString());
    }

    [Theory]
    [InlineData("""[]""", "rule", "a condition is an object, not a list.")]
    [InlineData("""{ "field": "output.x", "matches": "a.*" }""", "rule", "\"matches\" is not an operator.")]
    [InlineData("""{ "field": "output.x", "equals": 1, "in": [1] }""", "rule", "a field test has exactly one operator.")]
    [InlineData("""{ "field": "output..x", "equals": 1 }""", "rule.field", "the field is not a dotted path.")]
    [InlineData("""{ "field": "output.x", "gt": "5" }""", "rule.gt", "gt compares numbers only.")]
    [InlineData("""{ "field": "output.x", "in": "bug" }""", "rule.in", "in takes a list of values.")]
    [InlineData("""{ "field": "output.x", "exists": "yes" }""", "rule.exists", "exists takes true or false.")]
    [InlineData("""{ "all": [] }""", "rule.all", "all takes a non-empty list of conditions.")]
    [InlineData("""{ "all": [ { "field": "output.x", "equals": 1 } ], "any": [] }""", "rule", "a condition has exactly one of all, any or not.")]
    [InlineData("""{ "script": "return true" }""", "rule", "\"script\" is not part of the condition language.")]
    [InlineData("""{ "not": { "field": "output.x", "equals": { "a": 1 } } }""", "rule.not.equals", "equals compares with text, a number, true, false or null.")]
    public void Malformed_conditions_are_rejected_with_their_path(string json, string path, string problem)
    {
        var errors = new List<ConfigurationError>();
        using var document = JsonDocument.Parse(json);

        Assert.Null(ConditionParser.Parse(document.RootElement, "rule", errors));
        var error = Assert.Single(errors);
        Assert.Equal((path, problem), (error.Path, error.Problem));
    }

    [Fact]
    public void Every_mistake_in_a_condition_is_reported()
    {
        var errors = new List<ConfigurationError>();
        using var document = JsonDocument.Parse("""{ "any": [ { "field": "a", "gt": "x" }, { "nope": 1 } ] }""");

        ConditionParser.Parse(document.RootElement, "rule", errors);

        Assert.Equal(["rule.any[0].gt", "rule.any[1]"], errors.Select(error => error.Path));
    }

    [Fact]
    public void Fields_are_checked_against_the_schema_of_their_root()
    {
        var scope = Scope();

        Assert.Empty(ConditionChecker.Check(Parse("""{ "field": "output.items[0].name", "equals": "x" }"""), scope, "when"));
        Assert.Empty(ConditionChecker.Check(Parse("""{ "field": "checks.tests.passed", "equals": true }"""), scope, "when"));

        var error = Assert.Single(ConditionChecker.Check(Parse("""{ "field": "output.severty", "equals": "high" }"""), scope, "when"));
        Assert.Equal(ValidationPhase.Conditions, error.Phase);
        Assert.Equal("when.field", error.Path);
        Assert.Equal("field \"output.severty\" does not exist: output has no \"severty\".", error.Problem);
        Assert.EndsWith("Did you mean \"severity\"?", error.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "field": "output.severity", "gt": 3 }""", "field \"output.severity\" is string, so gt cannot compare it.")]
    [InlineData("""{ "field": "output.severity", "equals": "urgent" }""", "field \"output.severity\" is never \"urgent\": its values are \"low\", \"high\".")]
    [InlineData("""{ "field": "output.score", "equals": "high" }""", "field \"output.score\" is number, but is compared with \"high\", so the condition can never hold.")]
    [InlineData("""{ "field": "output.severity[0]", "equals": "x" }""", "field \"output.severity[0]\" does not exist: output.severity is not a list.")]
    [InlineData("""{ "field": "checks.build.passed", "equals": true }""", "field \"checks.build.passed\" does not exist: checks has no \"build\".")]
    [InlineData("""{ "field": "outcome", "equals": "done" }""", "field \"outcome\" is never \"done\": its values are \"completed\", \"handedOff\", \"failed\", \"cancelled\".")]
    [InlineData("""{ "field": "args.branch", "equals": "main" }""", "field \"args.branch\" starts with \"args\", which conditions cannot read here.")]
    public void Conditions_that_cannot_be_checked_or_can_never_hold_are_rejected(string json, string problem)
    {
        var error = Assert.Single(ConditionChecker.Check(Parse(json), Scope(), "when"));

        Assert.Equal(problem, error.Problem);
    }

    [Fact]
    public void A_condition_on_free_text_weakens_INV_01()
    {
        var scope = new ConditionScope().FreeText("output");

        var error = Assert.Single(ConditionChecker.Check(Parse("""{ "not": { "field": "output.text", "equals": "yes" } }"""), scope, "next[0].when"));

        Assert.Equal("next[0].when.not.field", error.Path);
        Assert.Contains("INV-01", error.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Paths_under_a_root_of_unknown_shape_are_not_checked()
    {
        var scope = new ConditionScope().Structured("task", null);

        Assert.Empty(ConditionChecker.Check(Parse("""{ "field": "task.anything[2].deep", "gte": 1 }"""), scope, "when"));
    }

    private static ConditionScope Scope() => new ConditionScope()
        .Structured("output", ConditionScope.Schema("""
            { "type": "object", "properties": {
                "severity": { "type": "string", "enum": ["low", "high"] },
                "score": { "type": "number" },
                "items": { "type": "array", "items": { "type": "object", "properties": { "name": { "type": "string" } } } } } }
            """))
        .Checks(["tests"])
        .Structured("outcome", ConditionScope.OutcomeSchema);

    private static Condition Parse(string json)
    {
        var errors = new List<ConfigurationError>();
        using var document = JsonDocument.Parse(json);
        var condition = ConditionParser.Parse(document.RootElement, "rule", errors);
        Assert.Empty(errors);
        return condition!;
    }
}
