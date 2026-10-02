using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// History across requests (CTX-06), kept in the conversation store (CAP-05), and shortened when the model reports it
/// too long (HIST). The agent <c>dev</c> is offered <c>read</c>, which returns the path it is given, and <c>edit</c>.
/// </summary>
public class HistoryTests
{
    private static readonly Stopped TooLong = new(StopReason.InputTooLong);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // CTX-06, CAP-05.
    [Theory]
    [InlineData(HistoryStrategy.None, new[] { "3" })]
    [InlineData(HistoryStrategy.Full, new[] { "1", "one", "2", "two", "3" })]
    [InlineData(HistoryStrategy.Shortened, new[] { "1", "one", "2", "two", "3" })]
    [InlineData(HistoryStrategy.LastTurns, new[] { "2", "two", "3" })]
    public async Task Each_strategy_starts_a_request_with_the_history_it_keeps(HistoryStrategy strategy, string[] expected)
    {
        var kit = Kit(new() { Strategy = strategy, LastTurns = 1 });
        kit.Model.Reply("one").Reply("two").Reply("three");

        foreach (var work in new[] { "1", "2", "3" })
        {
            await kit.RunAsync(Agent, work, Ct);
        }

        Assert.Equal(expected, kit.Model.Requests[2].History.Select(Text));
        Assert.Equal(strategy == HistoryStrategy.None ? 0 : 3, kit.Storage.Conversations.Turns.Count);
    }

    // SEC-02: a conversation is with one caller.
    [Fact]
    public async Task Each_caller_has_their_own_conversation_with_an_agent()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Full });
        kit.Model.Reply("one").Reply("two");

        await kit.Runner.RunAsync(Agent, "1", Owner, Ct);
        await kit.Runner.RunAsync(Agent, "2", Caller.Anonymous, Ct);

        Assert.Equal(["2"], kit.Model.Requests[1].History.Select(Text));
    }

    // CAP-02, TEST-08: the capability tools arrive with S16, which shows them absent when off.
    [Fact]
    public async Task A_capability_that_is_off_adds_no_storage()
    {
        var kit = new TestKit(new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { [Agent] = new() { Instructions = "Work." } } });
        kit.Model.Reply("Done.");

        await kit.RunAsync(Agent, "work", Ct);

        Assert.Empty(kit.Storage.Conversations.Turns);
    }

    // HIST-01, HIST-04, LOOP-03: by the provider's own mechanism, or by an application's shortener, which calls a model too.
    [Theory]
    [InlineData(HistoryOptions.Provider)]
    [InlineData("extension:Summarizer")]
    public async Task Input_too_long_shortens_the_history_and_the_call_is_made_again(string shortening)
    {
        var summarizer = new ScriptedModelProvider();
        var kit = new TestKit(
            Configure(new() { Strategy = HistoryStrategy.Shortened, Shortening = shortening }), Tools(),
            shorteners: new Dictionary<string, IHistoryShortener> { ["Summarizer"] = summarizer });
        kit.Model.Reply("one").Reply("two").Reply(TooLong).Reply("three").Reply("four");
        (shortening == HistoryOptions.Provider ? kit.Model : summarizer).Shorten(history => [Message.User("Summary: 1 and 2 are done."), history[^1]]);

        await kit.RunAsync(Agent, "1", Ct);
        await kit.RunAsync(Agent, "2", Ct);
        var result = await kit.RunAsync(Agent, "3", Ct);
        await kit.RunAsync(Agent, "4", Ct);

        Assert.Equal((AgentOutcome.Completed, "three"), (result.Outcome, result.Output));
        Assert.Equal(["1", "one", "2", "two", "3"], kit.Model.Requests[2].History.Select(Text));
        Assert.Equal(["Summary: 1 and 2 are done.", "3"], kit.Model.Requests[3].History.Select(Text));
        Assert.Equal(["Summary: 1 and 2 are done.", "3", "three", "4"], kit.Model.Requests[4].History.Select(Text));
    }

    // COST-02, HIST-01: a model call the shortening makes is paid like any other: it counts against the budgets and in the run's
    // cost, and is stored as a model call. The shortener is told where the current turn starts, which it keeps.
    [Fact]
    public async Task A_model_call_the_shortening_makes_is_counted_and_stored()
    {
        var summarizer = new PaidSummarizer();
        var kit = new TestKit(
            Configure(new() { Strategy = HistoryStrategy.Shortened, Shortening = "extension:Summarizer" }), Tools(),
            shorteners: new Dictionary<string, IHistoryShortener> { ["Summarizer"] = summarizer });
        kit.Model.Reply("one").Reply(TooLong).Reply("two");
        await kit.RunAsync(Agent, "1", Ct);
        var work = new Work(Agent, "2");

        var result = await kit.Runner.RunAsync(work, Ct);

        Assert.Equal((AgentOutcome.Completed, 4m), (result.Outcome, result.Statistics.Cost)); // a million input tokens of Opus 5.5
        Assert.Equal(2, summarizer.TurnStart);
        Assert.Contains(
            (await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(coreEvent => coreEvent.Payload),
            payload => payload is ModelCallEnded { Cost: 4m, Model: "claude-opus-5-5" });
    }

    // HIST-01, REL-01, COST-02: when the model summarizes a conversation itself, the summary is a model call through the gateway, so a
    // failed one is retried, and it is paid and stored. The summary, an assistant message of the provider's content, takes the earlier
    // turns' place before the current turn, and the operator's messages it summarized are told again after it.
    [Fact]
    public async Task A_models_own_summary_goes_through_the_gateway_and_takes_the_earlier_turns_place()
    {
        var kit = new TestKit(Configure(new() { Strategy = HistoryStrategy.Shortened }), Tools(), capabilities: new() { Summarizes = true });
        var summary = new ProviderContent(JsonDocument.Parse("""{ "type": "summary", "text": "1 is done." }""").RootElement.Clone());
        kit.Model.Reply("one").Reply(TooLong).Fail(ModelFailure.Transient)
            .Reply(new ContentReceived(summary), new UsageReported(new Usage(1_000_000, 0, 0, 0)), new Stopped(StopReason.Unknown)).Reply("two");
        kit.Runner.Send(Agent, Sender.Operator, "Use SQLite.");
        await kit.RunAsync(Agent, "1", Ct);
        var work = new Work(Agent, "2");

        var running = kit.Runner.RunAsync(work, Ct);
        while (!running.IsCompleted)
        {
            await Task.Delay(10, Ct);
            if (kit.Time.Pending.Count > 0)
            {
                kit.Time.Advance(kit.Time.Pending.Min()); // the gateway's wait before it tries again
            }
        }

        var result = await running;
        Assert.Equal((AgentOutcome.Completed, "two", 4m), (result.Outcome, result.Output, result.Statistics.Cost));
        var asked = kit.Model.Requests[3];
        Assert.True(asked.Summarize);
        Assert.Equal(kit.Model.Requests[1].History[..^1], asked.History); // the earlier turns, not the current one
        var retried = kit.Model.Requests[4].History;
        Assert.Equal(new Message(Role.Assistant, [summary]), retried[0]);
        Assert.Equal(["2", "<message from=\"operator\">\nUse SQLite.\n</message>"], retried.Skip(1).Select(Text));
        Assert.Contains(
            (await kit.Storage.Events.ReadAsync(null, work.RunId, 0, Ct)).Select(coreEvent => coreEvent.Payload), payload => payload is ModelCallRetried);
    }

    // CTX-10: the call made again has no new reply, so a turn-scoped context is not sent twice.
    [Fact]
    public async Task The_call_made_again_after_shortening_keeps_one_copy_of_a_turn_scoped_context()
    {
        var options = Configure(new() { Strategy = HistoryStrategy.Shortened });
        options = options with { Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Context = options.Agents[Agent].Context with { OperatingFacts = ["Be brief."] } } } };
        var kit = new TestKit(options, Tools(), capabilities: new() { TurnScopedMessages = true });
        kit.Model.Reply(TooLong).Reply("Done.").Shorten(history => history);

        await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(kit.Model.Requests[0].History, kit.Model.Requests[1].History);
    }

    // HIST-05: every tool request keeps a result, which may become a short note of what it held.
    [Fact]
    public async Task Shortening_may_clear_old_tool_results_keeping_a_note()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.CallTools(("read", """{ "path": "a.cs" }""")).Reply("one").Reply(TooLong).Reply("two");
        kit.Model.Shorten(history => history.Select(message => message.Content[0] is ToolResultContent result
            ? new Message(Role.User, [new ToolResultContent(result.ToolUseId, "[cleared: the text of a.cs]", isError: false)])
            : message));

        await kit.RunAsync(Agent, "1", Ct);
        var result = await kit.RunAsync(Agent, "2", Ct);

        Assert.Equal(AgentOutcome.Completed, result.Outcome);
        Assert.Equal("[cleared: the text of a.cs]", Assert.IsType<ToolResultContent>(kit.Model.Requests[3].History[2].Content[0]).Text);
    }

    // HIST-04: once only; with `full` not at all.
    [Theory]
    [InlineData(HistoryStrategy.Full, 1)]
    [InlineData(HistoryStrategy.Shortened, 2)]
    public async Task Input_still_too_long_after_one_shortening_ends_in_a_handoff(HistoryStrategy strategy, int calls)
    {
        var kit = Kit(new() { Strategy = strategy });
        kit.Model.Reply(TooLong).Reply(TooLong).Shorten(history => history);

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(
            (HandoffReason.ProviderFailure, "the input is too long for the model", calls), (result.Handoff!.Reason, result.Handoff.Detail, kit.Model.Requests.Count));
    }

    [Fact]
    public async Task A_failed_shortening_ends_in_a_handoff()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.Reply(TooLong);

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal(
            "the input is too long for the model, and shortening it failed: InvalidOperationException", result.Handoff!.Detail);
    }

    // HIST-02, TEST-15.
    [Theory]
    [InlineData("does not start with a user message or the provider's summary")]
    [InlineData("has a tool request without its result")]
    [InlineData("changes the current turn")]
    public async Task Shortened_history_that_is_not_valid_is_rejected(string problem)
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.CallTools(("read", """{ "path": "a.cs" }""")).Reply("one").Reply(TooLong);
        kit.Model.Shorten(history => problem switch
        {
            "does not start with a user message or the provider's summary" => history[1..],
            "has a tool request without its result" => [history[0], history[1], history[3], history[4]],
            _ => [.. history[..^1], Message.User("other work")],
        });

        await kit.RunAsync(Agent, "1", Ct);
        var result = await kit.RunAsync(Agent, "2", Ct);

        Assert.Equal($"the input is too long for the model, and the shortened history {problem}", result.Handoff!.Detail);
        Assert.Equal(3, kit.Model.Requests.Count);
        Assert.Equal(["1", "", "", "one", "2"], result.Transcript.Select(Text));
    }

    // HIST-03, TEST-15.
    [Fact]
    public async Task Shortening_leaves_the_stored_runs_events_audit_entries_task_boards_and_memory_as_they_were()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.CallTools(("edit", """{ "path": "a.cs" }""")).Reply("one").Reply(TooLong).Reply("two");
        kit.Model.Shorten(history => [Message.User("Summary: a.cs was edited."), history[^1]]);
        var first = new Work(Agent, "1") { Caller = Owner, TaskId = "t1" };
        await kit.Runner.Board(Owner.Tenant, first.RunId).AddAsync("t1", new() { Title = "Edit a.cs" }, "planned", Ct);
        await kit.Runner.RunAsync(first, Ct);
        await AddMemoryAsync(kit);
        var before = await kit.Storage.ExportAsync(Owner.Tenant, Owner.Id!, Ct);
        var memory = await kit.Runner.Memory(Owner).ReadAsync(Ct);

        await kit.Runner.RunAsync(Agent, "2", Owner, Ct);

        var after = await kit.Storage.ExportAsync(Owner.Tenant, Owner.Id!, Ct);
        Assert.NotEmpty(before.Audit);
        Assert.Equal(before.Runs, after.Runs.Take(before.Runs.Count));
        Assert.Equal(before.Events, after.Events.Take(before.Events.Count));
        Assert.Equal(before.Audit, after.Audit);
        Assert.NotEmpty(before.Tasks);
        Assert.Equal(JsonSerializer.Serialize(before.Tasks), JsonSerializer.Serialize(after.Tasks));
        Assert.Equal(JsonSerializer.Serialize(memory.Log), JsonSerializer.Serialize((await kit.Runner.Memory(Owner).ReadAsync(Ct)).Log));
        Assert.Equal("", kit.Model.Requests[^1].Memory); // the conversation began without memory, and is told of it instead
        Assert.Contains(kit.Model.Requests[^1].History, message => message.Role == Role.System);
    }

    // MEM-03, HIST-03: shortening may drop the earlier message that told of a change, so the next call tells again.
    [Fact]
    public async Task Memory_changes_are_told_again_after_the_history_is_shortened()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        await AddMemoryAsync(kit);
        kit.Model.Reply("one").Reply("two").Reply(TooLong).Reply("three").Shorten(history => [Message.User("Summary: 1 and 2 are done."), history[^1]]);
        await kit.Runner.RunAsync(Agent, "1", Owner, Ct);
        await kit.Runner.Memory(Owner).ProposeAsync(new(MemoryKind.Note, "style", "Spaces, not tabs."), Ct);
        Assert.True((await kit.Runner.Memory(Owner).ApproveAsync(2, null, Ct)).Accepted);
        await kit.Runner.RunAsync(Agent, "2", Owner, Ct);

        var result = await kit.Runner.RunAsync(Agent, "3", Owner, Ct);

        var told = "<message from=\"operator\">\nProject memory changed:\n- note #2 style: Spaces, not tabs.\n</message>";
        var requests = kit.Model.Requests;
        Assert.Equal((AgentOutcome.Completed, 4), (result.Outcome, requests.Count));
        Assert.Equal([told], requests[2].History.Where(message => message.Role == Role.System).Select(Text));
        Assert.Equal(["Summary: 1 and 2 are done.", "3", told], requests[3].History.Select(Text));
        Assert.Equal(requests[0].Memory, requests[3].Memory);
    }

    private static async Task AddMemoryAsync(TestKit kit)
    {
        var memory = kit.Runner.Memory(Owner);
        await memory.ProposeAsync(new(MemoryKind.Note, "build", "Run dotnet test."), Ct);
        await memory.ApproveAsync(1, null, Ct);
    }

    // HIST-01.
    [Fact]
    public void Shortening_needs_a_provider_that_can_shorten_or_a_registered_shortener()
    {
        var options = Configure(new() { Strategy = HistoryStrategy.Shortened });
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                [Agent] = options.Agents[Agent],
                ["other"] = options.Agents[Agent] with { Context = new() { History = new() { Strategy = HistoryStrategy.Shortened, Shortening = "extension:Summarizer" } } },
            },
        };

        var error = Assert.Throws<ConfigurationException>(() => new AgentRunner(
            options, new Dictionary<string, IModelProvider> { ["claude"] = new Unshortening() }, new InMemoryStorage(), Tools(),
            new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(),
            new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider()));

        Assert.Equal(
            [
                (ValidationPhase.References, "agents.other.context.history.shortening", "history shortener extension \"Summarizer\" is not registered."),
                (ValidationPhase.Provider, "agents.dev.context.history.shortening", $"provider \"claude\" cannot shorten history with model \"{options.Models[ModelProfile.DefaultName].Model}\"."),
            ],
            error.Errors.Select(error => (error.Phase, error.Path, error.Problem)));
    }

    /// <summary>The text of a message's first piece; empty for a piece that is not text.</summary>
    private static string Text(Message message) => message.Content[0] is TextContent text ? text.Text : "";

    private static TestKit Kit(HistoryOptions history) => new(Configure(history), Tools());

    private static OfficinaOptions Configure(HistoryOptions history)
    {
        var options = Options(("read", Extension("read")), ("edit", Extension("edit") with { GateExemption = "Tests only." }));
        return options with
        {
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Context = new() { History = history } } },
            Capabilities = new() { ConversationStore = new() { Enabled = true }, TaskBoard = new() { Enabled = true }, ProjectMemory = new() { Enabled = true } },
        };
    }

    private static Dictionary<string, ITool> Tools() => new() { ["read"] = new FakeTool(ToolKind.Read), ["edit"] = new FakeTool(ToolKind.Write) };

    /// <summary>A provider without a mechanism of its own to shorten history.</summary>
    private sealed class Unshortening : IModelProvider
    {
        public ProviderCapabilities CapabilitiesOf(string model) => ProviderCapabilities.None;

        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>A shortener that calls a model of its own to summarize the earlier turns, and keeps the current one.</summary>
    private sealed class PaidSummarizer : IHistoryShortener
    {
        public int TurnStart { get; private set; }

        public ValueTask<ShortenedHistory> ShortenAsync(ModelRequest request, CancellationToken ct)
        {
            TurnStart = request.TurnStart;
            return ValueTask.FromResult(new ShortenedHistory([Message.User("Summary."), .. request.History[request.TurnStart..]]) { Usage = new Usage(1_000_000, 0, 0, 0) });
        }
    }
}
