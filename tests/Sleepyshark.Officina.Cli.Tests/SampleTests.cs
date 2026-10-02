using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Cli.Tests;

/// <summary>
/// Every loop pattern has an example configuration in <c>samples/</c>, loaded with the CLI's loader and run offline
/// against the scripted model (TEST-06). The check <c>Samples.Short</c> fails output longer than 20 characters.
/// </summary>
public class SampleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Samples() =>
        [.. Directory.GetDirectories(Path.Combine(GeneratedDocumentationTests.Root, "samples")).Select(directory => Path.GetFileName(directory)!).Order(StringComparer.Ordinal)];

    // The team pattern runs from the team capability; until then its sample is only validated.
    [Theory]
    [MemberData(nameof(Samples))]
    public void Every_sample_is_valid(string sample) => Assert.Empty(Kit(sample).Errors);

    [Fact]
    public void There_is_a_sample_of_every_pattern_and_of_a_nested_one() =>
        Assert.Equal(
            ["evaluate-and-revise", "fan-out", "nested", "plan-and-execute", "router", "single-call", "team", "tool-loop", "workflow"],
            Samples().Select(row => row.Data));

    [Fact]
    public async Task Single_call() =>
        Assert.Equal((AgentOutcome.Completed, """{ "total": 42 }"""), await RunAsync("single-call", "extractor", """{ "total": 42 }"""));

    [Fact]
    public async Task Tool_loop()
    {
        var (kit, _) = Kit("tool-loop");
        kit.Model.CallTools(("note", """{ "text": "Refunds take 5 days." }""")).Reply("Five days.");

        var result = await kit.RunAsync("assistant", "How long do refunds take?", Ct);

        Assert.Equal((AgentOutcome.Completed, "Five days."), (result.Outcome, result.Output));
        Assert.Equal(new Finding("Refunds take 5 days."), Assert.Single(result.Record).Item);
    }

    [Theory]
    [InlineData("question", "Restart it.")]
    [InlineData("spam", """{ "kind": "spam" }""")]
    public async Task Workflow(string kind, string output) =>
        Assert.Equal((AgentOutcome.Completed, output), await RunAsync("workflow", "support", $$"""{ "kind": "{{kind}}" }""", "Restart it."));

    // PAT-03, TEST-07: a value without a route, and a classification that stays invalid, end in a handoff.
    [Theory]
    [InlineData("technical", null)]
    [InlineData("sales", HandoffReason.NoRouteForValue)]
    [InlineData(null, HandoffReason.InvalidStructuredOutput)]
    public async Task Router(string? desk, HandoffReason? reason)
    {
        var (kit, _) = Kit("router");
        if (desk is null)
        {
            kit.Model.Reply("billing").Reply("billing").Reply("billing");
        }
        else
        {
            kit.Model.Reply($$"""{ "desk": "{{desk}}" }""").Reply("Restart it.");
        }

        var result = await kit.RunAsync("helpdesk", "My screen is blank.", Ct);

        Assert.Equal(reason, result.Handoff?.Reason);
        Assert.Equal(reason is null ? "Restart it." : result.Handoff!.Detail, result.Output);
    }

    [Fact]
    public async Task Fan_out()
    {
        var (kit, _) = Kit("fan-out");
        kit.Model.Reply("""{ "verdict": false }""").Reply("""{ "verdict": true }""").Reply("""{ "verdict": false }""");

        var result = await kit.RunAsync("panel", "The moon is made of cheese.", Ct);

        Assert.Equal((AgentOutcome.Completed, """{ "verdict": false }""", 3), (result.Outcome, result.Output, result.Statistics.Iterations));
    }

    [Fact]
    public async Task Evaluate_and_revise() =>
        Assert.Equal((AgentOutcome.Completed, "Faster start."), await RunAsync("evaluate-and-revise", "writer", "Starts faster, much faster than before.", "Faster start."));

    [Fact]
    public async Task Plan_and_execute() =>
        Assert.Equal(
            (AgentOutcome.Completed, """["Built.","Tested."]"""),
            await RunAsync("plan-and-execute", "builder", """{ "steps": ["build", "test"] }""", "Built.", "Tested."));

    // PAT-02: a workflow step that is an evaluate-and-revise pattern; events name each step by its path (EVT-02).
    [Fact]
    public async Task Nested()
    {
        var (kit, _) = Kit("nested");
        kit.Model.Reply("Starts faster, much faster than before.").Reply("Faster start.").Reply("Démarrage plus rapide.");

        var result = await kit.RunAsync("release", "Version 2", Ct);

        Assert.Equal((AgentOutcome.Completed, "Démarrage plus rapide."), (result.Outcome, result.Output));
        Assert.Equal(
            [("draft/generate", "writer"), ("draft/generate", "writer"), ("draft", "release"), ("translate", "translator")],
            (await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs[^1].RunId, 0, Ct))
                .Where(coreEvent => coreEvent.Payload is StepEnded).Select(coreEvent => (coreEvent.Step!, coreEvent.Agent)));
    }

    private static async Task<(AgentOutcome, string)> RunAsync(string sample, string agent, params string[] replies)
    {
        var (kit, _) = Kit(sample);
        foreach (var reply in replies)
        {
            kit.Model.Reply(reply);
        }

        var result = await kit.RunAsync(agent, "work", Ct);
        return (result.Outcome, result.Output);
    }

    private static (TestKit Kit, IReadOnlyList<Core.Configuration.ConfigurationError> Errors) Kit(string sample)
    {
        var configuration = SofConfiguration.Load(Path.Combine(GeneratedDocumentationTests.Root, "samples", sample), null, new Dictionary<string, string>(), []);
        return (new TestKit(configuration.Options, checks: new Dictionary<string, ICheck> { ["Samples.Short"] = new Short() }), configuration.Errors);
    }

    private sealed class Short : ICheck
    {
        public ValueTask<CheckResult> RunAsync(CheckContext context, CancellationToken ct) =>
            ValueTask.FromResult(new CheckResult(context.Output!.Length <= 20, [$"{context.Output.Length} characters; at most 20"]));
    }
}
