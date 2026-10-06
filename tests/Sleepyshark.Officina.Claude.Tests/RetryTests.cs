using static Sleepyshark.Officina.Claude.Tests.StreamTests;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>Retries and error classes (MDL-04), on recorded HTTP responses; the clock never sleeps.</summary>
public class RetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rate_limit_waits_as_long_as_Retry_After_asks_then_succeeds()
    {
        var api = new FakeApi().Error(429, "rate_limit_error", retryAfter: TimeSpan.FromSeconds(7)).Stream(Sse.Text());
        var time = new InstantTime();
        using var model = Model(api, time);

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new ModelStopped(ModelStopReason.End), events[^1]);
        Assert.Equal([TimeSpan.FromSeconds(7)], time.Waits);
        Assert.Equal(2, api.Requests.Count);
        Assert.Equal(api.Requests[0], api.Requests[1]);
    }

    [Fact]
    public async Task A_long_Retry_After_is_capped()
    {
        var api = new FakeApi().Error(429, "rate_limit_error", retryAfter: TimeSpan.FromHours(1)).Stream(Sse.Text());
        var time = new InstantTime();
        using var model = Model(api, time);

        await CollectAsync(model, Hi);

        Assert.Equal([TimeSpan.FromSeconds(30)], time.Waits);
    }

    [Fact]
    public async Task Cancelling_during_a_retry_wait_ends_the_call_at_once()
    {
        var api = new FakeApi().Error(429, "rate_limit_error", retryAfter: TimeSpan.FromSeconds(20));
        var time = new InstantTime(held: true);
        using var model = Model(api, time);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var call = Task.Run(async () =>
        {
            await foreach (var _ in model.StreamAsync(Hi, cancel.Token))
            {
            }
        }, Ct);

        await time.Waiting.Task.WaitAsync(Ct);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Concurrent_calls_keep_their_Retry_After_apart()
    {
        var api = new FakeApi { Together = 2 }
            .Route("Call A", own => own.Error(429, "rate_limit_error", retryAfter: TimeSpan.FromSeconds(7)).Stream(Sse.Text()))
            .Route("Call B", own => own.Error(529, "overloaded_error").Stream(Sse.Text()));
        var time = new InstantTime();
        using var model = Model(api, time);
        Task<List<ModelEvent>> Call(string name) => Task.Run(() =>
        {
            InstantTime.Caller.Value = name;
            return CollectAsync(model, new([], "Answer briefly.", [Message.Of(Role.User, $"Call {name}")]));
        }, Ct);

        var callA = Call("A");
        await api.FirstRequest.Task.WaitAsync(Ct);
        var callB = Call("B");
        await Task.WhenAll(callA, callB);

        var waits = time.WaitsByCaller.ToDictionary(wait => wait.Caller!, wait => wait.Wait);
        Assert.Equal(TimeSpan.FromSeconds(7), waits["A"]);
        Assert.InRange(waits["B"], TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(408, "timeout_error")]
    [InlineData(409, "conflict_error")]
    public async Task A_request_timeout_or_conflict_is_retried(int status, string type)
    {
        var api = new FakeApi().Error(status, type).Stream(Sse.Text());
        using var model = Model(api, new InstantTime());

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new ModelStopped(ModelStopReason.End), events[^1]);
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task An_overload_backs_off_exponentially_then_succeeds()
    {
        var api = new FakeApi().Error(529, "overloaded_error").Error(500, "api_error").Stream(Sse.Text());
        var time = new InstantTime();
        using var model = Model(api, time);

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new ModelStopped(ModelStopReason.End), events[^1]);
        Assert.Collection(
            time.Waits,
            first => Assert.InRange(first, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1)),
            second => Assert.InRange(second, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task An_overload_mid_stream_restarts_the_reply_and_says_so()
    {
        var api = new FakeApi()
            .Stream(Sse.Events(
                Sse.Start,
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""",
                Sse.Error("overloaded_error")))
            .Stream(Sse.Text("end_turn", "Hello."));
        using var model = Model(api, new InstantTime());

        var events = await CollectAsync(model, Hi);

        Assert.Equal([new TextDelta("Hel"), new ModelRetried(), new TextDelta("Hello.")], events.Where(each => each is TextDelta or ModelRetried));
        Assert.Equal("Hello.", Assert.Single(events.OfType<BlockReceived>()).Block.Text);
    }

    [Fact]
    public async Task A_retry_before_anything_streamed_is_reported_too()
    {
        var api = new FakeApi().Error(529, "overloaded_error").Error(429, "rate_limit_error").Stream(Sse.Text());
        using var model = Model(api, new InstantTime());

        var events = await CollectAsync(model, Hi);

        Assert.Equal([new ModelRetried(), new ModelRetried(), new TextDelta("Hello.")], events.Take(3));
        Assert.Equal(new UsageReceived(new Usage(10, 5, 0, 0)), Assert.Single(events.OfType<UsageReceived>()));
    }

    [Fact]
    public async Task The_tokens_of_an_attempt_that_failed_mid_stream_are_reported_before_the_retry()
    {
        var api = new FakeApi()
            .Stream(Sse.Events(
                Sse.Start,
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""",
                """{"type":"message_delta","delta":{"stop_reason":null,"stop_sequence":null},"usage":{"output_tokens":3}}""",
                Sse.Error("overloaded_error")))
            .Stream(Sse.Text());
        using var model = Model(api, new InstantTime());

        var events = await CollectAsync(model, Hi);

        Assert.Equal(
            [new UsageReceived(new Usage(10, 3, 0, 0)), new ModelRetried(), new UsageReceived(new Usage(10, 5, 0, 0))],
            events.Where(each => each is UsageReceived or ModelRetried));
    }

    [Fact]
    public async Task The_tokens_of_a_last_attempt_that_failed_mid_stream_are_reported_before_the_failure()
    {
        var api = new FakeApi();
        for (var attempt = 0; attempt < ClaudeErrors.MaxAttempts; attempt++)
        {
            api.Stream(Sse.Events(Sse.Start, Sse.Error("overloaded_error")));
        }

        using var model = Model(api, new InstantTime());
        var events = new List<ModelEvent>();

        var failure = await Assert.ThrowsAsync<ClaudeException>(async () =>
        {
            await foreach (var modelEvent in model.StreamAsync(Hi, Ct))
            {
                events.Add(modelEvent);
            }
        });

        Assert.Equal(ClaudeFailure.Transient, failure.Failure);
        Assert.Equal(ClaudeErrors.MaxAttempts, events.OfType<UsageReceived>().Count());
        Assert.Equal(ClaudeErrors.MaxAttempts - 1, events.OfType<ModelRetried>().Count());
        Assert.Equal(new UsageReceived(new Usage(10, 1, 0, 0)), events[^1]);
    }

    [Fact]
    public async Task A_connection_dropped_mid_stream_restarts_the_reply()
    {
        var api = new FakeApi()
            .Dropped(Sse.Events(
                Sse.Start,
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}"""))
            .Stream(Sse.Text("end_turn", "Hello."));
        using var model = Model(api, new InstantTime());

        var events = await CollectAsync(model, Hi);

        Assert.Equal([new TextDelta("Hel"), new ModelRetried(), new TextDelta("Hello.")], events.Where(each => each is TextDelta or ModelRetried));
    }

    [Fact]
    public async Task A_restarted_reply_reaches_the_run_once_and_the_host_is_told()
    {
        var api = new FakeApi()
            .Stream(Sse.Events(
                Sse.Start,
                """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
                """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hel"}}""",
                Sse.Error("overloaded_error")))
            .Stream(Sse.Text("end_turn", "Hello."));
        using var model = Model(api, new InstantTime());
        var agent = new Agent { Model = model, Instructions = "Answer briefly." };

        var result = await agent.RunAsync(new Conversation(), "Hi", cancellationToken: Ct);

        Assert.Equal("Hello.", Assert.IsType<Completed>(result).Text);
    }

    [Fact]
    public async Task A_failure_that_persists_ends_with_a_transient_failure_after_every_attempt()
    {
        var api = new FakeApi();
        for (var attempt = 0; attempt < ClaudeErrors.MaxAttempts; attempt++)
        {
            api.Error(529, "overloaded_error");
        }

        using var model = Model(api, new InstantTime());

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => CollectAsync(model, Hi));

        Assert.Equal(ClaudeFailure.Transient, failure.Failure);
        Assert.Equal(ClaudeErrors.MaxAttempts, api.Requests.Count);
    }

    [Theory]
    [InlineData(400, "invalid_request_error", ClaudeFailure.InvalidRequest)]
    [InlineData(401, "authentication_error", ClaudeFailure.Authentication)]
    [InlineData(403, "permission_error", ClaudeFailure.Authentication)]
    [InlineData(404, "not_found_error", ClaudeFailure.InvalidRequest)]
    public async Task Errors_that_retrying_cannot_fix_are_classified_and_not_retried(int status, string type, ClaudeFailure expected)
    {
        var api = new FakeApi().Error(status, type);
        using var model = Model(api, new InstantTime());

        var failure = await Assert.ThrowsAsync<ClaudeException>(() => CollectAsync(model, Hi));

        Assert.Equal(expected, failure.Failure);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task A_prompt_longer_than_the_context_window_stops_as_context_full()
    {
        using var model = Model(new FakeApi().Error(400, "invalid_request_error", "prompt is too long: 1000001 tokens > 1000000 maximum"));

        var events = await CollectAsync(model, Hi);

        Assert.Equal([new ModelStopped(ModelStopReason.ContextFull)], events);
    }

    [Fact]
    public async Task A_failed_run_carries_the_failure_s_class()
    {
        using var model = Model(new FakeApi().Error(401, "authentication_error", "invalid x-api-key"));
        var agent = new Agent { Model = model, Instructions = "Answer." };

        var result = await agent.RunAsync(new Conversation(), "Hi", cancellationToken: Ct);

        Assert.Contains("Authentication", Assert.IsType<Failed>(result).Error, StringComparison.Ordinal);
    }
}
