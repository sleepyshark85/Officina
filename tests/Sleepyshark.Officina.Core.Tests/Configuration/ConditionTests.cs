using Sleepyshark.Officina.Core.Configuration;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Configuration;

/// <summary>The condition language (CFG-13): what each test holds for, and how a condition is checked against the schema it reads.</summary>
public class ConditionTests
{
    private const string Arguments = """{ "branch": "main", "count": 3, "force": true, "labels": ["bug"], "draft": null }""";

    private const string Schema = """
        {
          "type": "object",
          "properties": {
            "branch": { "type": "string" },
            "count": { "type": "integer" },
            "force": { "type": "boolean" },
            "labels": { "type": "array", "items": { "type": "string", "enum": ["bug", "feature"] } },
            "draft": { "type": ["boolean", "null"] }
          }
        }
        """;

    public static TheoryData<Condition, bool> Tests => new()
    {
        { new() { Field = "args.branch", Is = "main" }, true },
        { new() { Field = "args.branch", Is = "Main" }, false },
        { new() { Field = "args.count", Is = "3.0" }, true },
        { new() { Field = "args.force", Is = "true" }, true },
        { new() { Field = "args.force", Is = "1" }, false },
        { new() { Field = "args.branch", In = ["main", "master"] }, true },
        { new() { Field = "args.labels[0]", In = ["feature"] }, false },
        { new() { Field = "args.count", Gt = 3 }, false },
        { new() { Field = "args.count", Gte = 3 }, true },
        { new() { Field = "args.count", Gte = 0, Lt = 3 }, false },
        { new() { Field = "args.count", Lt = 3.5m }, true },
        { new() { Field = "args.count", Lte = 2 }, false },
        { new() { Field = "args.branch", Gt = 0 }, false },
        { new() { Field = "args.branch", Exists = true }, true },
        { new() { Field = "args.draft", Exists = true }, false },
        { new() { Field = "args.missing", Exists = false }, true },
        { new() { Field = "args.missing", Is = "x" }, false },
        { new() { Field = "args.labels[5]", Is = "bug" }, false },
        { new() { All = [new() { Field = "args.force", Is = "true" }, new() { Field = "args.count", Gt = 1 }] }, true },
        { new() { Any = [new() { Field = "args.force", Is = "false" }, new() { Field = "args.count", Gt = 5 }] }, false },
        { new() { Not = new() { Field = "args.missing", Is = "x" } }, true },
    };

    [Theory]
    [MemberData(nameof(Tests))]
    public void Each_test_holds_as_documented(Condition condition, bool holds) => Assert.Equal(holds, condition.Holds(Args(Arguments)));

    [Fact]
    public void A_condition_that_fits_the_schema_has_no_problems() =>
        Assert.Empty(new Condition
        {
            Any = [new() { Field = "args.labels[0]", In = ["bug"] }, new() { Field = "args.count", Gte = 2, Lt = 10 }, new() { Field = "args.draft", Is = "true" }],
        }.Check(Args(Schema)));

    public static TheoryData<Condition, string> Problems => new()
    {
        { new() { Field = "args.title", Is = "x" }, "field args.title is not in the tool's arguments." },
        { new() { Field = "args.branch.name", Exists = true }, "field args.branch.name is not in the tool's arguments." },
        { new() { Field = "output.kind", Is = "x" }, "field output.kind is not a path into the tool's arguments, such as args.branch." },
        { new() { Field = "args.count", Is = "three" }, "field args.count can never be \"three\"." },
        { new() { Field = "args.labels[0]", In = ["bug", "chore"] }, "field args.labels[0] can never be \"chore\"." },
        { new() { Field = "args.branch", Gt = 1 }, "field args.branch is not a number, so gt, gte, lt and lte can never hold." },
        { new() { Field = "args.branch", Is = "main", In = ["dev"] }, "needs either a field and exactly one test, or exactly one of all, any and not." },
        { new() { Is = "main" }, "needs either a field and exactly one test, or exactly one of all, any and not." },
        { new() { Not = new() { Field = "args.force", Is = "yes" } }, "field args.force can never be \"yes\"." },
    };

    [Theory]
    [MemberData(nameof(Problems))]
    public void A_condition_is_checked_against_the_schema_it_reads(Condition condition, string problem) =>
        Assert.Equal(problem, Assert.Single(condition.Check(Args(Schema))));
}
