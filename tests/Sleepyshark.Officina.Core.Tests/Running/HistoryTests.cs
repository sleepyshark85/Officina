using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
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

    // CAP-02, TEST-08.
    [Fact]
    public async Task A_capability_that_is_off_adds_no_tools_or_storage()
    {
        var kit = new TestKit(new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { [Agent] = new() { Instructions = "Work." } } });
        kit.Model.Reply("Done.");

        await kit.RunAsync(Agent, "work", Ct);

        Assert.Empty(kit.Model.Requests[0].Tools);
        Assert.Empty(kit.Storage.Conversations.Turns);
    }

    // HIST-01, HIST-04, LOOP-03.
    [Fact]
    public async Task Input_too_long_shortens_the_history_through_the_provider_and_the_call_is_made_again()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.Reply("one").Reply("two").Reply(TooLong).Reply("three").Reply("four");
        kit.Model.Shorten(history => [Message.User("Summary: 1 and 2 are done."), history[^1]]);

        await kit.RunAsync(Agent, "1", Ct);
        await kit.RunAsync(Agent, "2", Ct);
        var result = await kit.RunAsync(Agent, "3", Ct);
        await kit.RunAsync(Agent, "4", Ct);

        Assert.Equal((AgentOutcome.Completed, "three"), (result.Outcome, result.Output));
        Assert.Equal(["1", "one", "2", "two", "3"], kit.Model.Requests[2].History.Select(Text));
        Assert.Equal(["Summary: 1 and 2 are done.", "3"], kit.Model.Requests[3].History.Select(Text));
        Assert.Equal(["Summary: 1 and 2 are done.", "3", "three", "4"], kit.Model.Requests[4].History.Select(Text));
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
    [InlineData("does not start with a user message")]
    [InlineData("has a tool request without its result")]
    [InlineData("changes the current turn")]
    public async Task Shortened_history_that_is_not_valid_is_rejected(string problem)
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.CallTools(("read", """{ "path": "a.cs" }""")).Reply("one").Reply(TooLong);
        kit.Model.Shorten(history => problem switch
        {
            "does not start with a user message" => history[1..],
            "has a tool request without its result" => [history[0], history[1], history[3], history[4]],
            _ => [.. history[..^1], Message.User("other work")],
        });

        await kit.RunAsync(Agent, "1", Ct);
        var result = await kit.RunAsync(Agent, "2", Ct);

        Assert.Equal($"the input is too long for the model, and the shortened history {problem}", result.Handoff!.Detail);
        Assert.Equal(3, kit.Model.Requests.Count);
        Assert.Equal(["1", "", "", "one", "2"], result.Transcript.Select(Text));
    }

    // HIST-03, TEST-15: S06 adds the run record, S18 tasks and S17 memory.
    [Fact]
    public async Task Shortening_leaves_the_stored_runs_events_and_audit_entries_as_they_were()
    {
        var kit = Kit(new() { Strategy = HistoryStrategy.Shortened });
        kit.Model.CallTools(("edit", """{ "path": "a.cs" }""")).Reply("one").Reply(TooLong).Reply("two");
        kit.Model.Shorten(history => [Message.User("Summary: a.cs was edited."), history[^1]]);
        await kit.Runner.RunAsync(Agent, "1", Owner, Ct);
        var before = await kit.Storage.ExportAsync(Owner.Tenant, Owner.Id!, Ct);

        await kit.Runner.RunAsync(Agent, "2", Owner, Ct);

        var after = await kit.Storage.ExportAsync(Owner.Tenant, Owner.Id!, Ct);
        Assert.NotEmpty(before.Audit);
        Assert.Equal(before.Runs, after.Runs.Take(before.Runs.Count));
        Assert.Equal(before.Events, after.Events.Take(before.Events.Count));
        Assert.Equal(before.Audit, after.Audit);
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
            new Dictionary<string, IGate>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(),
            new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider()));

        Assert.Equal(
            [
                (ValidationPhase.References, "agents.other.context.history.shortening", "history shortener extension \"Summarizer\" is not registered."),
                (ValidationPhase.Provider, "agents.dev.context.history.shortening", "provider \"claude\" cannot shorten history."),
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
            Capabilities = new() { ConversationStore = new() { Enabled = true } },
        };
    }

    private static Dictionary<string, ITool> Tools() => new() { ["read"] = new FakeTool(ToolKind.Read), ["edit"] = new FakeTool(ToolKind.Write) };

    /// <summary>A provider without a mechanism of its own to shorten history.</summary>
    private sealed class Unshortening : IModelProvider
    {
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;

        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
