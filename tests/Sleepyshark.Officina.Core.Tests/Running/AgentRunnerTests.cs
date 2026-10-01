using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

public class AgentRunnerTests
{
    private const string Instructions = "Extract the invoice number, date and total. Reply as JSON.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_agent_with_only_instructions_completes_with_the_model_output()
    {
        var kit = Extractor();
        kit.Model.Reply("""{"number":"A-17"}""");

        var result = await kit.RunAsync("extractor", "Invoice A-17", Ct);

        Assert.Equal((AgentOutcome.Completed, """{"number":"A-17"}"""), (result.Outcome, result.Output));
    }

    [Fact]
    public async Task An_agent_with_only_instructions_runs_on_the_default_profile()
    {
        var kit = Extractor();
        kit.Model.Reply("done");

        await kit.RunAsync("extractor", "Invoice A-17", Ct);

        var request = Assert.Single(kit.Model.Requests);
        Assert.Same(kit.Runner.Options.Models[ModelProfile.DefaultName], request.Profile);
        Assert.StartsWith(Instructions + "\n\n", request.Instructions);
        Assert.Equal([Message.User("Invoice A-17")], request.History);
    }

    [Fact]
    public async Task Agents_in_one_application_can_use_different_providers()
    {
        var first = new ScriptedModelProvider().Reply("from first");
        var second = new ScriptedModelProvider().Reply("from second");
        var options = new OfficinaOptions
        {
            Providers = new Dictionary<string, ProviderOptions> { ["first"] = new(), ["second"] = new() },
            Models = new Dictionary<string, ModelProfile>
            {
                ["cheap"] = new() { Provider = "first", Model = "small" },
                ["strong"] = new() { Provider = "second", Model = "large" },
            },
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["classifier"] = new() { Instructions = "Classify.", Model = "cheap" },
                ["answerer"] = new() { Instructions = "Answer.", Model = "strong" },
            },
        };
        var providers = new Dictionary<string, IModelProvider> { ["first"] = first, ["second"] = second };
        var runner = new AgentRunner(
            options, providers, new InMemoryRunStore(), new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, IKnowledgeSource>(),
            new InMemoryAuditLog(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider());

        var cheap = await runner.RunAsync("classifier", "input", ct: Ct);
        var strong = await runner.RunAsync("answerer", "input", ct: Ct);

        Assert.Equal("from first", cheap.Output);
        Assert.Equal("from second", strong.Output);
        Assert.Equal("small", Assert.Single(first.Requests).Profile.Model);
        Assert.Equal("large", Assert.Single(second.Requests).Profile.Model);
    }

    [Fact]
    public void An_unknown_profile_is_reported_by_name()
    {
        var error = Assert.Throws<ConfigurationException>(() => Extractor(new AgentDefinition { Instructions = Instructions, Model = "missing" }));

        Assert.Contains("\"missing\"", error.Message, StringComparison.Ordinal);
    }

    // LOOP-01, LOOP-02.
    [Fact]
    public async Task Turns_of_one_agent_wait_for_each_other_while_other_agents_run()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = new FakeTool(ToolKind.Read, run: async (_, _) =>
        {
            reading.SetResult();
            await release.Task;
            return ToolResult.Success("ok");
        });
        var options = Options(("read", Extension("read")));
        options = options with { Agents = new Dictionary<string, AgentDefinition>(options.Agents) { ["other"] = new() { Instructions = "Other." } } };
        var kit = new TestKit(options, new Dictionary<string, ITool> { ["read"] = read });
        kit.Model.CallTools(("read", "{}"));

        var first = kit.RunAsync(Agent, "first", Ct);
        await reading.Task;
        var second = kit.RunAsync(Agent, "second", Ct);
        Assert.Single(kit.Model.Requests);

        kit.Model.Reply("other done");
        Assert.Equal("other done", (await kit.RunAsync("other", "third", Ct)).Output);

        kit.Model.Reply("first done").Reply("second done");
        release.SetResult();
        Assert.Equal(["first done", "second done"], (await Task.WhenAll(first, second)).Select(result => result.Output));
    }

    // LOOP-09.
    [Fact]
    public async Task Cancelling_during_tool_calls_gives_every_tool_request_a_cancelled_result()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var read = new FakeTool(ToolKind.Read, run: async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return ToolResult.Success("never");
        });
        var kit = new TestKit(Options(("read", Extension("read"))), new Dictionary<string, ITool> { ["read"] = read });
        kit.Model.CallTools(("read", """{ "path": "a.cs" }"""), ("read", """{ "path": "b.cs" }"""));

        var result = await kit.RunAsync(Agent, "work", cancel.Token);

        Assert.Equal(HandoffReason.RequestedByHuman, result.Handoff!.Reason);
        Assert.Equal(
            new Message(Role.User, [new ToolResultContent("call-1", "cancelled", true), new ToolResultContent("call-2", "cancelled", true)]),
            result.Transcript[^1]);
        Assert.All(result.Handoff.ToolCalls, attempt => Assert.Equal(ToolErrorCategory.Cancelled, attempt.Result.Error));
    }

    [Fact]
    public async Task Cancelling_before_the_turn_starts_hands_off_without_calling_the_model()
    {
        var kit = Extractor();

        var result = await kit.RunAsync("extractor", "work", new CancellationToken(canceled: true));

        Assert.Equal((HandoffReason.RequestedByHuman, 0), (result.Handoff!.Reason, kit.Model.Requests.Count));
    }

    // REL-02.
    [Fact]
    public async Task A_failing_model_call_ends_in_a_handoff()
    {
        var kit = Extractor();

        var result = await kit.RunAsync("extractor", "work", Ct);

        Assert.Equal(
            (AgentOutcome.HandedOff, HandoffReason.ProviderFailure, "the model call failed: InvalidOperationException"),
            (result.Outcome, result.Handoff!.Reason, result.Output));
    }

    [Fact]
    public async Task An_extension_that_throws_ends_the_run_as_failed_with_every_tool_request_answered()
    {
        var options = Options(("edit", Extension("edit") with { Gates = ["broken"] })) with
        {
            Gates = new Dictionary<string, GateOptions> { ["broken"] = new() { Use = "extension:broken" } },
        };
        var kit = new TestKit(options, new Dictionary<string, ITool> { ["edit"] = new FakeTool(ToolKind.Write) }, new Dictionary<string, IGate> { ["broken"] = new BrokenGate() });
        kit.Model.CallTools(("edit", "{}"));

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Failed, "the turn failed: InvalidOperationException"), (result.Outcome, result.Output));
        Assert.Equal(new Message(Role.User, [new ToolResultContent("call-1", "cancelled", true)]), result.Transcript[^1]);
    }

    /// <summary>A kit whose only agent is <c>extractor</c>; by default it has only instructions.</summary>
    private static TestKit Extractor(AgentDefinition? agent = null) =>
        new(new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = agent ?? new() { Instructions = Instructions } } });

    private sealed class BrokenGate : IGate
    {
        public ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct) => throw new InvalidOperationException("broken");
    }
}
