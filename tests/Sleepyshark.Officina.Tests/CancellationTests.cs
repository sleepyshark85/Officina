using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

public class CancellationTests
{
    [Fact]
    public async Task Cancelling_mid_stream_appends_nothing_of_the_reply_and_the_conversation_stays_usable()
    {
        var model = new ScriptedModel()
            .Reply(new TextDelta("Par"), new TextDelta("is"), new BlockReceived(ScriptedModel.TextBlock("Paris")), new ModelStopped(ModelStopReason.End))
            .Reply("Hello again.");
        var agent = Agents.With(model);
        var conversation = new Conversation();
        using var cancellation = new CancellationTokenSource();

        var events = new List<RunEvent>();
        await foreach (var runEvent in agent.StreamAsync(conversation, "Capital of France?", cancellationToken: cancellation.Token))
        {
            events.Add(runEvent);
            if (runEvent is TextStreamed)
            {
                await cancellation.CancelAsync();
            }
        }

        Assert.Equal(new Stopped(StopReason.Cancelled, null, default), Assert.IsType<RunEnded>(events[^1]).Result);
        Assert.Equal([Role.User], events.OfType<ConversationAppended>().Select(appended => appended.Message.Role));
        Assert.Equal([Role.User], conversation.Messages.Select(message => message.Role));

        var next = await agent.RunAsync(conversation, "Hi", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hello again.", Assert.IsType<Completed>(next).Text);
        Assert.Equal([Role.User, Role.User, Role.Assistant], conversation.Messages.Select(message => message.Role));
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
}
