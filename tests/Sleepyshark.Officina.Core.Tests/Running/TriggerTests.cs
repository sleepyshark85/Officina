using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>How work reaches an agent (TRG-01…04, MDL-10, EGR-04).</summary>
public class TriggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // TRG-01, TRG-02.
    [Fact]
    public async Task The_same_definition_behaves_the_same_under_every_trigger()
    {
        // Tool arguments are parsed anew for each run, so what was sent is compared as text.
        var runs = new List<(AgentOutcome Outcome, string Output, string Sent)>();
        foreach (var trigger in Enum.GetValues<Trigger>())
        {
            var kit = new TestKit(Options(("read", Extension("read"))), new Dictionary<string, ITool> { ["read"] = new FakeTool(ToolKind.Read) });
            kit.Model.CallTools(("read", "{}")).Reply("Done.");

            var result = trigger == Trigger.Batch
                ? Assert.Single(await kit.Runner.RunBatchAsync(Agent, ["work"], ct: Ct))
                : await kit.Runner.RunAsync(new Work(Agent, "work") { Trigger = trigger }, Ct);

            var sent = kit.Model.Requests.SelectMany(request => request.History).Select(message => $"{message.Role}: {string.Join(", ", message.Content)}");
            runs.Add((result.Outcome, result.Output, string.Join('\n', sent)));
        }

        Assert.Equal(6, runs.Count);
        Assert.All(runs, run => Assert.Equal((AgentOutcome.Completed, "Done.", runs[0].Sent), run));
    }

    // TRG-03, TRG-04, MDL-10.
    [Fact]
    public async Task A_batch_reports_a_result_per_input_runs_stateless_and_may_use_batch_processing()
    {
        var model = new EchoModel();
        var runner = new AgentRunner(
            Options(), new Dictionary<string, IModelProvider> { ["claude"] = model }, new InMemoryStorage(), new Dictionary<string, ITool>(),
            new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(),
            new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider());

        var results = await runner.RunBatchAsync(Agent, ["first", "broken", "third"], ct: Ct);
        var single = await runner.RunAsync(Agent, "fourth", ct: Ct);

        Assert.Equal(
            [(AgentOutcome.Completed, "done: first"), (AgentOutcome.HandedOff, "the model call failed: IOException"), (AgentOutcome.Completed, "done: third")],
            results.Select(result => (result.Outcome, result.Output)));
        Assert.Equal("done: fourth", single.Output);

        // Each input starts with nothing but itself: no stored conversation joins them.
        Assert.All(model.Requests, request => Assert.Single(request.History));
        Assert.Equal([true, true, true, false], model.Requests.Select(request => request.Batch));
    }

    // EGR-04.
    [Fact]
    public async Task A_request_flagged_for_a_human_hands_off_without_calling_the_model()
    {
        var kit = new TestKit(Options());

        var result = await kit.Runner.RunAsync(new Work(Agent, "Refund me.") { HandOffToHuman = true }, Ct);

        Assert.Equal((HandoffReason.RequestedByHuman, ToolResult.Human, "Refund me."), (result.Handoff!.Reason, result.Handoff.To, result.Handoff.Work));
        Assert.Empty(kit.Model.Requests);
    }

    [Fact]
    public async Task The_host_knows_the_run_id_before_the_run_starts_and_the_run_acts_for_the_caller()
    {
        var kit = new TestKit(Options());
        kit.Model.Reply("Done.");
        var work = new Work(Agent, "work") { Caller = Owner };

        await kit.Runner.RunAsync(work, Ct);

        var run = Assert.Single(kit.Storage.Runs.Runs);
        Assert.Equal((work.RunId, "owner"), (run.RunId, run.Owner));
        Assert.NotEmpty(await kit.Storage.Events.ReadAsync("acme", work.RunId, 0, Ct));
    }

    /// <summary>A model that answers with the work it is given, and fails on work that says it is broken.</summary>
    private sealed class EchoModel : IModelProvider
    {
        private readonly ConcurrentQueue<ModelRequest> requests = new();

        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public IReadOnlyList<ModelRequest> Requests => [.. requests];

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            requests.Enqueue(request);
            var work = ((TextContent)request.History[0].Content[0]).Text;
            await Task.Yield();
            if (work == "broken")
            {
                throw new IOException("unavailable");
            }

            yield return new TextDelta($"done: {work}");
            yield return new Stopped(StopReason.Finished);
        }
    }
}
