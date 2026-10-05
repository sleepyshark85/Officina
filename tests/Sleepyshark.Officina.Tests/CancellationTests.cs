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

        Assert.Equal(new Stopped(StopReason.Cancelled, null, default), Assert.IsType<RunEnded>(events[^1]).Result);
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

        Assert.Equal(new Stopped(StopReason.Cancelled, null, default), result);
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
}
