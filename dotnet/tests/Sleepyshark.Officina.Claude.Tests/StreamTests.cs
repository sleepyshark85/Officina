using System.Text.Json;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>How a streamed reply maps to the model contract's events.</summary>
public class StreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static ModelRequest Hi => new(new RequestPrefix("test", [], "Answer briefly."), [Message.Of(Role.User, "Hi")]);

    internal static ClaudeModel Model(FakeApi api, TimeProvider? time = null) =>
        new("test-key", api, time) { Model = "claude-opus-5-5", Effort = ClaudeEffort.Medium };

    internal static async Task<List<ModelEvent>> CollectAsync(ClaudeModel model, ModelRequest request)
    {
        var events = new List<ModelEvent>();
        await foreach (var modelEvent in model.StreamAsync(request, Ct))
        {
            events.Add(modelEvent);
        }

        return events;
    }

    [Fact]
    public async Task Text_streams_as_it_arrives_and_every_block_comes_complete_with_its_raw_JSON()
    {
        using var model = Model(new FakeApi().Fixture("thinking-text-tool.sse"));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(
            [
                new TextDelta("Looking up "), new TextDelta("«Café Libro»."),
                new BlockReceived(new ContentBlock(null, """{"type":"thinking","signature":"EqQBCkYIBRgCKkB\u002Bsig/a==","thinking":""}""")),
                new BlockReceived(new ContentBlock("Looking up «Café Libro».", """{"type":"text","text":"Looking up \u00ABCaf\u00E9 Libro\u00BB."}""")),
                new BlockReceived(new ContentBlock(
                    null,
                    """{"type":"tool_use","id":"toolu_01","name":"search","input":{"query":"Gaudy Night"}}""",
                    new ToolCall("toolu_01", "search", """{"query":"Gaudy Night"}"""))),
                new UsageReceived(new Usage(12, 42, 2048, 300)),
                new ModelStopped(ModelStopReason.ToolUse),
            ],
            events);
    }

    [Fact]
    public async Task Cache_writes_kept_for_an_hour_are_counted_apart_as_they_cost_more()
    {
        var start = Sse.Start.Replace(
            """{"input_tokens":10,"output_tokens":1}""",
            """{"input_tokens":10,"cache_creation_input_tokens":300,"cache_creation":{"ephemeral_5m_input_tokens":100,"ephemeral_1h_input_tokens":200},"output_tokens":1}""",
            StringComparison.Ordinal);
        using var model = Model(new FakeApi().Stream(Sse.Text().Replace(Sse.Start, start, StringComparison.Ordinal)));

        var events = await CollectAsync(model, Hi);

        var usage = Assert.Single(events.OfType<UsageReceived>()).Usage;
        Assert.Equal(new Usage(10, 5, 0, 300, CacheWriteHour: 200), usage);
        Assert.Equal(0.00004m + 0.0001m + 0.0005m + 0.0016m, model.Price!.Cost(usage));
    }

    [Fact]
    public async Task Cache_writes_kept_for_an_hour_are_counted_apart_in_every_iteration_of_a_compacting_call()
    {
        var delta = """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":2,"cache_creation_input_tokens":500,"output_tokens":5,"iterations":["""
            + """{"type":"compaction","input_tokens":40,"cache_read_input_tokens":0,"cache_creation_input_tokens":1000,"cache_creation":{"ephemeral_5m_input_tokens":200,"ephemeral_1h_input_tokens":800},"output_tokens":300},"""
            + """{"type":"message","input_tokens":2,"cache_read_input_tokens":0,"cache_creation_input_tokens":500,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":500},"output_tokens":5}]}}""";
        var sse = Sse.Text().Replace(
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}""", delta, StringComparison.Ordinal);
        using var model = Model(new FakeApi().Stream(sse));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new Usage(42, 305, 0, 1_500, CacheWriteHour: 1_300), Assert.Single(events.OfType<UsageReceived>()).Usage);
    }

    [Fact]
    public async Task An_iteration_of_a_kind_the_SDK_does_not_know_still_counts_its_tokens()
    {
        var delta = """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"input_tokens":2,"output_tokens":5,"iterations":["""
            + """{"type":"a_kind_from_the_future","input_tokens":30,"cache_read_input_tokens":10,"cache_creation_input_tokens":0,"output_tokens":20},"""
            + """{"type":"message","input_tokens":2,"cache_read_input_tokens":0,"cache_creation_input_tokens":0,"output_tokens":5}]}}""";
        var sse = Sse.Text().Replace(
            """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}""", delta, StringComparison.Ordinal);
        using var model = Model(new FakeApi().Stream(sse));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new Usage(32, 25, 10, 0), Assert.Single(events.OfType<UsageReceived>()).Usage);
    }

    [Fact]
    public void Opus_5_5_is_priced_by_default_and_a_host_may_give_another_price()
    {
        using var listed = new ClaudeModel("test-key") { Model = "claude-opus-5-5", Effort = ClaudeEffort.Medium };
        using var negotiated = new ClaudeModel("test-key") { Model = "claude-opus-5-5", Effort = ClaudeEffort.Medium, Price = new ModelPrice(3m, 15m, 0.15m, 3.75m, 6m) };
        using var unknown = new ClaudeModel("test-key") { Model = "claude-unknown", Effort = ClaudeEffort.Medium };

        Assert.Equal(new ModelPrice(4m, 20m, 0.20m, 5m, 8m), listed.Price);
        Assert.Equal(3m, negotiated.Price!.Input);
        Assert.Null(unknown.Price);
    }

    [Fact]
    public async Task A_refusal_carries_its_category()
    {
        using var model = Model(new FakeApi().Fixture("refusal.sse"));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new ModelStopped(ModelStopReason.Refusal, "cyber"), events[^1]);
    }

    [Fact]
    public async Task Usage_adds_up_every_iteration_of_the_call_and_a_compaction_block_is_kept_raw()
    {
        using var model = Model(new FakeApi().Fixture("compaction-iterations.sse"));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new UsageReceived(new Usage(50, 663, 5230, 50648)), Assert.Single(events.OfType<UsageReceived>()));
        var compaction = events.OfType<BlockReceived>().First().Block;
        Assert.Null(compaction.Text);
        Assert.Equal("Summary: the customer asked about shelf Q2.", JsonDocument.Parse(compaction.Raw!).RootElement.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("end_turn", ModelStopReason.End, null)]
    [InlineData("tool_use", ModelStopReason.ToolUse, null)]
    [InlineData("max_tokens", ModelStopReason.MaxTokens, null)]
    [InlineData("model_context_window_exceeded", ModelStopReason.ContextFull, null)]
    [InlineData("pause_turn", ModelStopReason.Unknown, "pause_turn")]
    [InlineData("a_reason_from_the_future", ModelStopReason.Unknown, "a_reason_from_the_future")]
    public async Task Stop_reasons_map_by_their_raw_word_and_an_unknown_one_keeps_it(string reason, ModelStopReason expected, string? detail)
    {
        using var model = Model(new FakeApi().Stream(Sse.Text(reason)));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new ModelStopped(expected, detail), events[^1]);
    }

    [Fact]
    public void The_settings_name_every_setting_that_shapes_a_request()
    {
        using var five = Model(new FakeApi());
        using var hour = new ClaudeModel("k")
        {
            Model = "claude-opus-5-5",
            Effort = ClaudeEffort.Medium,
            PrefixCacheLifetime = CacheLifetime.OneHour,
            ConversationCacheLifetime = CacheLifetime.OneHour,
        };
        using var mixed = new ClaudeModel("k") { Model = "claude-opus-5-5", Effort = ClaudeEffort.Medium, PrefixCacheLifetime = CacheLifetime.OneHour };

        // One word when the lifetimes are the same, as before they could differ, so stored conversations keep their fingerprint.
        Assert.Equal("claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=5m thinking=adaptive", five.Settings);
        Assert.Equal("claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=1h thinking=adaptive", hour.Settings);
        Assert.Equal("claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=1h/5m thinking=adaptive", mixed.Settings);
    }
}
