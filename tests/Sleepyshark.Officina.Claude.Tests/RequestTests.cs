using System.Text.Json;
using static Sleepyshark.Officina.Claude.Tests.StreamTests;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>The request layout, checked against golden JSON.</summary>
public class RequestTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Blocks as the adapter stores them: the SDK's JSON, with non-ASCII and <c>+</c> escaped.</summary>
    private static readonly ContentBlock Thinking = new(null, ReadRaw("thinking"));

    private static readonly ContentBlock Text = new("Looking up «Café Libro».", ReadRaw("text"));

    private static ModelRequest Request => new(
        new RequestPrefix(
            "test",
            [
                new Tool("search", "Searches the catalogue.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""", ToolKind.Read, NoHandler),
                new Tool("add_to_cart", "Adds a book to the cart.", """{"type":"object","properties":{"isbn":{"type":"string"},"copies":{"type":"integer"}},"required":["isbn"]}""", ToolKind.Write, NoHandler),
            ],
            "You are the assistant of a bookshop."),
        [
            Message.Of(Role.User, "Is Gaudy Night in stock?"),
            Message.Of(Role.Operator, "Today is 2026-10-05."),
            new Message(Role.Assistant, [Thinking, Text]),
            Message.Of(Role.User, "And its price?"),
            Message.Of(Role.Operator, "Today is 2026-10-05."),
        ]);

    [Fact]
    public void The_request_body_matches_the_golden_layout()
    {
        using var model = new ClaudeModel("test-key") { Model = "claude-opus-5-5", Effort = ClaudeEffort.High, MaxOutputTokens = 8000, CacheLifetime = CacheLifetime.OneHour };

        var body = JsonSerializer.Serialize(ClaudeRequest.Build(model, Request).RawBodyData, Indented);

        Assert.Equal(Golden("request-layout.json"), body.ReplaceLineEndings("\n"));
    }

    [Theory]
    [InlineData(null, 8000)]
    [InlineData(500, 500)]
    [InlineData(20_000, 8000)]
    public void The_output_limit_is_the_lower_of_the_model_s_and_the_budget_s(int? budget, long expected)
    {
        using var model = new ClaudeModel("test-key") { Model = "claude-opus-5-5", Effort = ClaudeEffort.High, MaxOutputTokens = 8000 };

        var body = ClaudeRequest.Build(model, Request with { MaxOutputTokens = budget }).RawBodyData;

        Assert.Equal(expected, body["max_tokens"].GetInt64());
    }

    [Fact]
    public async Task Stored_blocks_reach_the_wire_byte_for_byte_and_the_request_streams()
    {
        var api = new FakeApi().Stream(Sse.Text());
        using var model = Model(api);

        await CollectAsync(model, Request);

        var sent = Assert.Single(api.Requests);
        Assert.Contains(Thinking.Raw!, sent, StringComparison.Ordinal);
        Assert.Contains(Text.Raw!, sent, StringComparison.Ordinal);
        Assert.True(JsonDocument.Parse(sent).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task A_reply_is_stored_and_replayed_unchanged_on_the_next_request()
    {
        var api = new FakeApi().Fixture("thinking-text.sse").Stream(Sse.Text());
        using var model = Model(api);
        var agent = new Agent { Model = model, Instructions = "Answer briefly." };
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "Find Gaudy Night.", cancellationToken: Ct);
        var stored = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        await agent.RunAsync(stored, "Thanks.", cancellationToken: Ct);

        var replayed = conversation.Messages[^1].Blocks;
        Assert.Equal(2, replayed.Length);
        Assert.Contains("[" + string.Join(",", replayed.Select(block => block.Raw)) + "]", api.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_results_go_back_as_one_user_message_in_call_order_with_is_error_on_failures()
    {
        using var model = new ClaudeModel("test-key") { Model = "claude-opus-5-5", Effort = ClaudeEffort.Medium };
        var call = new ContentBlock(null, """{"type":"tool_use","id":"toolu_01","name":"search","input":{"query":"x"}}""", new ToolCall("toolu_01", "search", """{"query":"x"}"""));
        var request = new ModelRequest(new RequestPrefix("test", [], "Help."),
        [
            Message.Of(Role.User, "Find x."),
            new Message(Role.Assistant, [call]),
            new Message(Role.User, [new ContentBlock(new ToolResult("toolu_01", "Found x.", false)), new ContentBlock(new ToolResult("toolu_02", "No such book.", true)), new ContentBlock(new ToolResult("toolu_03", "", false))]),
            Message.Of(Role.User, "Thanks."),
        ]);

        var messages = ClaudeRequest.Build(model, request).RawBodyData["messages"];

        Assert.Equal(
            """[{"role":"user","content":[{"type":"text","text":"Find x."}]},"""
            + """{"role":"assistant","content":[{"type":"tool_use","id":"toolu_01","name":"search","input":{"query":"x"}}]},"""
            + """{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_01","content":"Found x."},"""
            + """{"type":"tool_result","tool_use_id":"toolu_02","content":"No such book.","is_error":true},{"type":"tool_result","tool_use_id":"toolu_03"}]},"""
            + """{"role":"user","content":[{"type":"text","text":"Thanks."}]}]""",
            messages.GetRawText());
    }

    private static Task<ToolOutput> NoHandler(JsonElement input, CancellationToken cancellationToken) => throw new NotSupportedException();

    private static string ReadRaw(string name) =>
        JsonDocument.Parse(Golden("blocks.json")).RootElement.GetProperty(name).GetString()!;

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).ReplaceLineEndings("\n");
}
