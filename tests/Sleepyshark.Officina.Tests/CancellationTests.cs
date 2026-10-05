using System.Text.Json;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

public class CancellationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScriptedModel StreamingParis() => new ScriptedModel().Reply(
        new TextDelta("Par"), new TextDelta("is"), new BlockReceived(ScriptedModel.TextBlock("Paris")), new ModelStopped(ModelStopReason.End));

    [Fact]
    public async Task Cancelling_mid_stream_appends_nothing_and_the_next_run_sends_a_valid_request()
    {
        var model = StreamingParis().Reply("Hello again.");
        var agent = Agents.With(model);
        var conversation = new Conversation();
        using var cancellation = new CancellationTokenSource();

        var events = new List<RunEvent>();
        await foreach (var runEvent in agent.StreamAsync(conversation, "Capital of France?", "Date: 2026-10-05.", cancellation.Token))
        {
            events.Add(runEvent);
            if (runEvent is TextStreamed)
            {
                await cancellation.CancelAsync();
            }
        }

        Assert.Equal(new Stopped(StopReason.Cancelled, null, default), Agents.Outcome(Assert.IsType<RunEnded>(events[^1]).Result));
        Assert.Empty(events.OfType<ConversationAppended>());
        Assert.Empty(conversation.Messages);

        var next = await agent.RunAsync(conversation, "Hi", "Date: 2026-10-05.", Ct);

        Assert.Equal("Hello again.", Assert.IsType<Completed>(next).Text);
        Assert.Equal([Role.User, Role.Operator], model.Requests[1].Messages.Select(message => message.Role));
        Assert.Equal([Role.User, Role.Operator, Role.Assistant], conversation.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task A_run_cancelled_before_it_starts_appends_nothing_and_calls_no_model()
    {
        var model = new ScriptedModel().Reply("Hello.");
        var conversation = new Conversation();

        var result = await Agents.With(model).RunAsync(conversation, "Hi", cancellationToken: new CancellationToken(canceled: true));

        Assert.Equal(new Stopped(StopReason.Cancelled, null, default), Agents.Outcome(result));
        Assert.Empty(conversation.Messages);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task A_second_run_on_a_conversation_in_use_throws_and_the_first_one_finishes()
    {
        var agent = Agents.With(StreamingParis().Reply("Hello."));
        var conversation = new Conversation();

        await using var first = agent.StreamAsync(conversation, "Capital of France?", cancellationToken: Ct).GetAsyncEnumerator(Ct);
        Assert.True(await first.MoveNextAsync());
        Assert.IsType<TextStreamed>(first.Current);

        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.RunAsync(conversation, "Hi", cancellationToken: Ct));

        RunEvent last = first.Current;
        while (await first.MoveNextAsync())
        {
            last = first.Current;
        }

        Assert.Equal("Paris", Assert.IsType<Completed>(Assert.IsType<RunEnded>(last).Result).Text);
        Assert.IsType<Completed>(await agent.RunAsync(conversation, "Thanks", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_conversation_saved_while_its_tools_ran_gets_error_results_for_them_when_resumed()
    {
        // The host saved the conversation on each append; the application then stopped while the tools ran.
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "search", """{"query":"x"}"""), new ToolCall("c2", "search", """{"query":"y"}""")).Reply("Resumed.");
        var sink = new RecordingSink();
        var agent = Agents.With(model, tools: Agents.SearchTool()) with { AuditSink = sink };
        var conversation = new Conversation();
        string? saved = null;
        await foreach (var runEvent in agent.StreamAsync(conversation, "Search.", cancellationToken: Ct))
        {
            if (runEvent is ConversationAppended appended)
            {
                saved = JsonSerializer.Serialize(appended.Conversation);
            }

            if (runEvent is ToolCallStarted)
            {
                break;
            }
        }

        var resumed = JsonSerializer.Deserialize<Conversation>(saved!)!;
        var events = await Agents.CollectAsync(agent.StreamAsync(resumed, "Go on.", cancellationToken: Ct));

        Assert.Equal("Resumed.", Assert.IsType<Completed>(Assert.IsType<RunEnded>(events[^1]).Result).Text);
        var results = Assert.IsType<ConversationAppended>(events[0]).Message.Blocks.Select(block => block.ToolResult).ToList();
        Assert.Equal([new ToolResult("c1", RunEngine.Interrupted, true), new ToolResult("c2", RunEngine.Interrupted, true)], results);
        Assert.Null(RoleSequence.Problem(model.Requests[^1].Messages));
        Assert.Empty(PrefixStability.Problems(model.Requests));
        Assert.Equal(
            [("c1", "interrupted"), ("c2", "interrupted")],
            sink.Entries.Where(entry => entry.Kind == AuditKind.ToolEnded && entry.Outcome == "interrupted").Select(entry => (entry.CallId, entry.Outcome)));
    }
}
