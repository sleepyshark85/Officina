using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Events;

/// <summary>
/// The event stream (TEST-28): events identify their run and agent and are in order, a reader catches up after a
/// reconnect, and a stalled reader does not slow the agent. The agent <c>dev</c> is offered <c>read</c>, which waits
/// for the test to let it finish.
/// </summary>
public class EventBusTests
{
    private readonly TaskCompletionSource reading = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TestKit kit;

    public EventBusTests()
    {
        var read = new FakeTool(ToolKind.Read, run: async (_, _) =>
        {
            reading.SetResult();
            await release.Task;
            return ToolResult.Success("ok");
        });
        // Every kind is stored, so a reader that catches up sees the streamed text too.
        var options = Options(("read", Extension("read"))) with { Storage = new() { UnstoredEvents = [] } };
        kit = new TestKit(options, new Dictionary<string, ITool> { ["read"] = read });
        kit.Model.CallTools(("read", """{"path":"a.txt"}"""));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Kinds { get; } =
        ["turnStarted", "modelCallEnded", "toolCallStarted", "toolCallEnded", "textGenerated", "textGenerated", "modelCallEnded", "turnEnded"];

    // EVT-01, EVT-02, MDL-07.
    [Fact]
    public async Task Events_identify_their_run_and_agent_and_are_in_order()
    {
        var (run, runId) = await StartAsync();
        release.SetResult();
        await run;

        var events = await ReadTurnAsync(kit.Runner.Events.ReadAsync(null, runId, 0, Ct));

        Assert.Equal(Kinds, events.Select(read => read.Payload.Kind));
        Assert.All(events, read => Assert.Equal((runId, Agent), (read.RunId, read.Agent)));
        Assert.Equal(events.Select(read => read.Sequence).Order(), events.Select(read => read.Sequence));
        Assert.Equal([new TextGenerated("Fou"), new TextGenerated("nd.")], events.Select(read => read.Payload).OfType<TextGenerated>());
        Assert.Equal(new ToolCallStarted("read", """{"path":"a.txt"}"""), events[2].Payload);
    }

    // EVT-03.
    [Fact]
    public async Task A_reader_that_reconnects_catches_up_from_the_last_event_it_saw()
    {
        var (_, runId) = await StartAsync();
        var first = new List<CoreEvent>();
        await foreach (var read in kit.Runner.Events.ReadAsync(null, runId, 0, Ct))
        {
            first.Add(read);
            if (first.Count == 2)
            {
                break;
            }
        }

        release.SetResult();
        var rest = await ReadTurnAsync(kit.Runner.Events.ReadAsync(null, runId, first[^1].Sequence, Ct));

        Assert.Equal(Kinds, first.Concat(rest).Select(read => read.Payload.Kind));
    }

    // EVT-04.
    [Fact]
    public async Task A_stalled_reader_does_not_slow_the_agent_and_catches_up_later()
    {
        // Many more events than a reader's queue holds.
        kit.Model.Reply([.. Enumerable.Repeat<ModelEvent>(new TextDelta("x"), 2500), new Stopped(StopReason.Finished)]);
        var (run, runId) = await StartAsync(scripted: false);
        await using var stalled = kit.Runner.Events.ReadAsync(null, runId, 0, Ct).GetAsyncEnumerator(Ct);
        Assert.True(await stalled.MoveNextAsync());

        release.SetResult();
        Assert.Equal(2500, (await run).Output.Length);

        var events = new List<CoreEvent> { stalled.Current };
        while (events[^1].Payload is not TurnEnded && await stalled.MoveNextAsync())
        {
            events.Add(stalled.Current);
        }

        Assert.Equal(2506, events.Count);
        Assert.Equal(events.Select(read => read.Sequence).Order(), events.Select(read => read.Sequence));
    }

    // EVT-05.
    [Fact]
    public async Task Streamed_text_is_not_stored_by_default()
    {
        var kit = new TestKit(new OfficinaOptions
        {
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = new() { Instructions = "Work." } },
        });
        kit.Model.Reply("Done.");

        await kit.RunAsync(Agent, "work", Ct);

        var runId = kit.Storage.Runs.Runs.Single().RunId;
        Assert.Equal(["turnStarted", "modelCallEnded", "turnEnded"], (await kit.Storage.Events.ReadAsync(null, runId, 0, Ct)).Select(read => read.Payload.Kind));
    }

    [Fact]
    public void An_unknown_kind_of_event_is_reported_with_the_kinds_that_exist()
    {
        var error = Assert.Single(new OfficinaOptions { Storage = new() { UnstoredEvents = ["text"] } }.Validate());

        Assert.Equal(("storage.unstoredEvents", "\"text\" is not a kind of event."), (error.Path, error.Problem));
        Assert.StartsWith("Use one of: budgetWarning, cacheHitWarning, checkRan, checkpointTaken, humanAnswered, humanAsked, ", error.Fix, StringComparison.Ordinal);
    }

    /// <summary>Starts a run whose tool call waits for <see cref="release"/>; its id is known once the tool is called.</summary>
    /// <param name="scripted">Whether the model replies with the text of <see cref="Kinds"/> after the tool call.</param>
    private async Task<(Task<AgentResult> Run, string RunId)> StartAsync(bool scripted = true)
    {
        if (scripted)
        {
            kit.Model.Reply(new TextDelta("Fou"), new TextDelta("nd."), new Stopped(StopReason.Finished));
        }

        var run = kit.RunAsync(Agent, "work", Ct);
        await reading.Task;
        return (run, kit.Storage.Runs.Runs.Single().RunId);
    }

    private static async Task<List<CoreEvent>> ReadTurnAsync(IAsyncEnumerable<CoreEvent> events)
    {
        var read = new List<CoreEvent>();
        await foreach (var coreEvent in events)
        {
            read.Add(coreEvent);
            if (coreEvent.Payload is TurnEnded)
            {
                break;
            }
        }

        return read;
    }
}
