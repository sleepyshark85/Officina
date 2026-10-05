using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

public class RunTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scripted_multi_turn_conversation_completes_with_text()
    {
        var model = new ScriptedModel().Reply("Hello!").Reply("Paris.").Reply("You're welcome.");
        var agent = Agents.With(model);
        var conversation = new Conversation();

        var results = new List<RunResult>();
        foreach (var message in new[] { "Hi", "Capital of France?", "Thanks" })
        {
            results.Add(await agent.RunAsync(conversation, message, cancellationToken: Ct));
        }

        Assert.Equal(["Hello!", "Paris.", "You're welcome."], results.Select(result => Assert.IsType<Completed>(result).Text));
        Assert.Equal(
            [
                (Role.User, "Hi"), (Role.Assistant, "Hello!"), (Role.User, "Capital of France?"), (Role.Assistant, "Paris."),
                (Role.User, "Thanks"), (Role.Assistant, "You're welcome."),
            ],
            conversation.Messages.Select(message => (message.Role, message.Text)));

        // Each request carries the whole conversation so far, and the definition's instructions.
        Assert.Equal([1, 3, 5], model.Requests.Select(request => request.Messages.Length));
        Assert.All(model.Requests, request => Assert.Equal(Agents.Instructions, request.Instructions));
    }

    [Fact]
    public async Task An_agent_with_only_a_model_and_instructions_runs_statelessly()
    {
        var agent = new AgentDefinition { Model = new ScriptedModel().Reply("positive"), Instructions = "Classify the sentiment." };

        var result = await agent.RunAsync(new Conversation(), "I love it", cancellationToken: Ct);

        Assert.Equal("positive", Assert.IsType<Completed>(result).Text);
    }

    [Fact]
    public async Task Events_stream_text_and_usage_then_each_append_with_the_reply_and_the_result_last()
    {
        var model = new ScriptedModel().Reply(
            new TextDelta("Hel"),
            new TextDelta("lo"),
            new BlockReceived(ScriptedModel.TextBlock("Hello")),
            new UsageReceived(new Usage(100, 5, 0, 900)),
            new UsageReceived(new Usage(0, 7, 0, 0)),
            new ModelStopped(ModelStopReason.End));
        var conversation = new Conversation();

        var events = await Agents.CollectAsync(Agents.With(model).StreamAsync(conversation, "Hi", "Today is Monday.", Ct));

        Assert.Collection(
            events,
            e => Assert.Equal("Hel", Assert.IsType<TextStreamed>(e).Text),
            e => Assert.Equal("lo", Assert.IsType<TextStreamed>(e).Text),
            e => Assert.Equal(new Usage(100, 5, 0, 900), Assert.IsType<UsageReported>(e).Usage),
            e => Assert.Equal(new Usage(0, 7, 0, 0), Assert.IsType<UsageReported>(e).Usage),
            e => Assert.Equal(Role.User, Assert.IsType<ConversationAppended>(e).Message.Role),
            e => Assert.Equal(Role.Operator, Assert.IsType<ConversationAppended>(e).Message.Role),
            e => Assert.Equal(Role.Assistant, Assert.IsType<ConversationAppended>(e).Message.Role),
            e => Assert.Equal(new Usage(100, 12, 0, 900), Assert.IsType<Completed>(Assert.IsType<RunEnded>(e).Result).Usage));
        Assert.All(events.OfType<ConversationAppended>(), e => Assert.Same(conversation, e.Conversation));
    }

    [Fact]
    public async Task Run_context_is_appended_as_an_operator_message_after_the_user_message()
    {
        var model = new ScriptedModel().Reply("Good morning, Ana.");

        await Agents.With(model).RunAsync(new Conversation(), "Hi", "Date: 2026-10-05. Staff: Ana.", Ct);

        Assert.Equal(
            [(Role.User, "Hi"), (Role.Operator, "Date: 2026-10-05. Staff: Ana.")],
            model.Requests[0].Messages.Select(message => (message.Role, message.Text)));
        Assert.Equal(Agents.Instructions, model.Requests[0].Instructions);
    }

    [Fact]
    public async Task Reply_blocks_are_appended_exactly_as_received()
    {
        var thinking = new ContentBlock(null, """{"type":"thinking","thinking":"Hmm","signature":"c2ln+/="}""");
        var text = ScriptedModel.TextBlock("Done.");
        var model = new ScriptedModel().Reply(new BlockReceived(thinking), new BlockReceived(text), new ModelStopped(ModelStopReason.End));
        var conversation = new Conversation();

        var result = await Agents.With(model).RunAsync(conversation, "Go", cancellationToken: Ct);

        Assert.Equal("Done.", Assert.IsType<Completed>(result).Text);
        Assert.Equal([thinking, text], conversation.Messages[^1].Blocks);
    }

    public static TheoryData<ModelStopped, RunResult> StopReasons => new()
    {
        { new ModelStopped(ModelStopReason.End), new Completed("partial", default) },
        { new ModelStopped(ModelStopReason.MaxTokens), new Stopped(StopReason.OutputLimit, null, default) },
        { new ModelStopped(ModelStopReason.Refusal, "cyber"), new Stopped(StopReason.Refusal, "cyber", default) },
        { new ModelStopped(ModelStopReason.ContextFull), new Stopped(StopReason.ContextFull, null, default) },
        {
            new ModelStopped(ModelStopReason.ToolUse),
            new Failed(FailureReason.UnexpectedStop, "The model stopped to use tools but called none.", default)
        },
        {
            new ModelStopped(ModelStopReason.Unknown, "pause_turn"),
            new Failed(FailureReason.UnexpectedStop, "The model stopped for a reason the run cannot act on: pause_turn.", default)
        },
    };

    [Theory]
    [MemberData(nameof(StopReasons))]
    public async Task Every_stop_reason_maps_to_its_result_and_the_reply_is_kept(ModelStopped stop, RunResult expected)
    {
        var model = new ScriptedModel().Reply(new TextDelta("partial"), new BlockReceived(ScriptedModel.TextBlock("partial")), stop);
        var conversation = new Conversation();

        var result = await Agents.With(model).RunAsync(conversation, "Hi", cancellationToken: Ct);

        Assert.Equal(expected, Agents.Outcome(result));
        Assert.Equal([Role.User, Role.Assistant], conversation.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task A_model_failure_ends_the_run_as_failed_and_appends_nothing()
    {
        var model = new ScriptedModel().Fail(new InvalidOperationException("Overloaded after retries."), new TextDelta("Par"));
        var conversation = new Conversation();

        var result = await Agents.With(model).RunAsync(conversation, "Hi", cancellationToken: Ct);

        Assert.Equal(new Failed(FailureReason.ModelError, "Overloaded after retries.", default), Agents.Outcome(result));
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task A_reply_without_a_stop_reason_fails_the_run_and_is_not_appended()
    {
        var model = new ScriptedModel().Reply(new BlockReceived(ScriptedModel.TextBlock("Hi")));
        var conversation = new Conversation();

        var result = await Agents.With(model).RunAsync(conversation, "Hi", cancellationToken: Ct);

        Assert.Equal(FailureReason.ModelError, Assert.IsType<Failed>(result).Reason);
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task A_model_with_no_reply_left_fails_the_run_instead_of_throwing()
    {
        var result = await Agents.With(new ScriptedModel()).RunAsync(new Conversation(), "Hi", cancellationToken: Ct);

        Assert.Contains("no reply left", Assert.IsType<Failed>(result).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_that_throws_before_streaming_fails_the_run_instead_of_throwing()
    {
        var agent = new AgentDefinition { Model = new ThrowingModel(), Instructions = Agents.Instructions };
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Hi", cancellationToken: Ct);

        Assert.Equal(new Failed(FailureReason.ModelError, "Bad settings.", default), Agents.Outcome(result));
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task An_end_with_no_content_appends_nothing()
    {
        var model = new ScriptedModel().Reply(new ModelStopped(ModelStopReason.End));
        var conversation = new Conversation();

        var result = await Agents.With(model).RunAsync(conversation, "Hi", "Date: 2026-10-05.", Ct);

        Assert.Equal(new Completed("", default), Agents.Outcome(result));
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task After_a_failed_run_with_context_the_next_request_is_valid()
    {
        var model = new ScriptedModel().Fail(new InvalidOperationException("Overloaded.")).Reply("Hello.");
        var agent = Agents.With(model);
        var conversation = new Conversation();

        Assert.IsType<Failed>(await agent.RunAsync(conversation, "Hi", "Date: 2026-10-05.", Ct));
        var result = await agent.RunAsync(conversation, "Hi again", "Date: 2026-10-05.", Ct);

        Assert.Equal("Hello.", Assert.IsType<Completed>(result).Text);
        Assert.Equal([Role.User, Role.Operator], model.Requests[1].Messages.Select(message => message.Role));
        Assert.Equal([Role.User, Role.Operator, Role.Assistant], conversation.Messages.Select(message => message.Role));
    }

    [Fact]
    public async Task Whitespace_only_messages_and_context_are_rejected()
    {
        var agent = Agents.With(new ScriptedModel());

        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync(new Conversation(), " ", cancellationToken: Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => agent.RunAsync(new Conversation(), "Hi", "\n", Ct));
    }

    [Fact]
    public async Task The_scripted_model_rejects_role_sequences_the_API_rejects()
    {
        ModelRequest Request(params Role[] roles) => new([], "A", [.. roles.Select(role => Message.Of(role, "x"))]);
        var model = new ScriptedModel();

        foreach (var request in new[]
        {
            Request(Role.Assistant), Request(Role.User, Role.User), Request(Role.User, Role.Operator, Role.User),
            Request(Role.User, Role.Assistant, Role.Operator),
        })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in model.StreamAsync(request, Ct))
                {
                }
            });
        }
    }

    [Fact]
    public async Task A_rejected_request_leaves_its_scripted_reply_for_the_next_one()
    {
        var model = new ScriptedModel().Reply("Hello.");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in model.StreamAsync(new([], "A", [Message.Of(Role.Assistant, "x")]), Ct))
            {
            }
        });

        var result = await Agents.With(model).RunAsync(new Conversation(), "Hi", cancellationToken: Ct);

        Assert.Equal("Hello.", Assert.IsType<Completed>(result).Text);
    }

    [Fact]
    public async Task A_restarted_reply_discards_what_came_before_the_restart()
    {
        var model = new ScriptedModel().Reply(
            new TextDelta("Hel"), new BlockReceived(ScriptedModel.TextBlock("Hel")), new ModelRetried(),
            new TextDelta("Hello."), new BlockReceived(ScriptedModel.TextBlock("Hello.")), new ModelStopped(ModelStopReason.End));
        var conversation = new Conversation();

        var events = await Agents.CollectAsync(Agents.With(model).StreamAsync(conversation, "Hi", cancellationToken: Ct));

        Assert.Equal(
            [new TextStreamed("Hel"), new ReplyRestarted(), new TextStreamed("Hello.")],
            events.Where(runEvent => runEvent is TextStreamed or ReplyRestarted));
        Assert.Equal(new Completed("Hello.", default), Agents.Outcome(Assert.IsType<RunEnded>(events[^1]).Result));
        Assert.Equal([ScriptedModel.TextBlock("Hello.")], conversation.Messages[^1].Blocks);
    }

    [Fact]
    public async Task A_reply_is_kept_when_closing_the_call_fails_after_the_stop_reason()
    {
        var agent = new AgentDefinition { Model = new FailingDisposeModel(), Instructions = Agents.Instructions };
        var conversation = new Conversation();

        var result = await agent.RunAsync(conversation, "Hi", cancellationToken: Ct);

        Assert.Equal(new Completed("Hello.", default), Agents.Outcome(result));
        Assert.Equal([Role.User, Role.Assistant], conversation.Messages.Select(message => message.Role));
    }

    /// <summary>A model whose reply completes, and whose call then fails to close.</summary>
    private sealed class FailingDisposeModel : IModel
    {
        public string Settings => "failing-dispose";

        public string Provider => "test";

        public string Name => "failing-dispose";

        public ModelPrice? Price => null;

        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) => new Reply();

        private sealed class Reply : IAsyncEnumerable<ModelEvent>, IAsyncEnumerator<ModelEvent>
        {
            private readonly Queue<ModelEvent> events = new([new BlockReceived(ScriptedModel.TextBlock("Hello.")), new ModelStopped(ModelStopReason.End)]);

            public ModelEvent Current { get; private set; } = null!;

            public IAsyncEnumerator<ModelEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync() =>
                ValueTask.FromResult(events.TryDequeue(out var next) && (Current = next) is not null);

            public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("The connection reset while closing."));
        }
    }

    /// <summary>A model that fails before it returns a stream, as one that checks its request eagerly may.</summary>
    private sealed class ThrowingModel : IModel
    {
        public string Settings => "throwing";

        public string Provider => "test";

        public string Name => "throwing";

        public ModelPrice? Price => null;

        public IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken) =>
            throw new ArgumentException("Bad settings.");
    }
}
