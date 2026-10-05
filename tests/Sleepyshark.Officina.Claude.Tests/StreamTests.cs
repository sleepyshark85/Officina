using System.Text.Json;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>How a streamed reply maps to the model contract's events (MDL-01, MDL-05, MDL-06, CTX-05).</summary>
public class StreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static ModelRequest Hi => new([], "Answer briefly.", [Message.Of(Role.User, "Hi")]);

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
        using var hour = new ClaudeModel("k") { Model = "claude-opus-5-5", Effort = ClaudeEffort.Medium, CacheLifetime = CacheLifetime.OneHour };

        Assert.Equal("claude model=claude-opus-5-5 effort=medium max_tokens=64000 cache=5m thinking=adaptive", five.Settings);
        Assert.NotEqual(five.Settings, hour.Settings);
    }
}
