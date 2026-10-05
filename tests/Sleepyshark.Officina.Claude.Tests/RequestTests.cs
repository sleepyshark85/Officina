using System.Text.Json;
using static Sleepyshark.Officina.Claude.Tests.StreamTests;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>The request layout (CTX-01, CTX-02, CTX-03, MDL-05), checked against golden JSON.</summary>
public class RequestTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Blocks as the adapter stores them: the SDK's JSON, with non-ASCII and <c>+</c> escaped.</summary>
    private static readonly ContentBlock Thinking = new(null, ReadRaw("thinking"));

    private static readonly ContentBlock Text = new("Looking up «Café Libro».", ReadRaw("text"));

    private static ModelRequest Request => new(
        [
            new Tool("search", "Searches the catalogue.", """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
            new Tool("add_to_cart", "Adds a book to the cart.", """{"type":"object","properties":{"isbn":{"type":"string"},"copies":{"type":"integer"}},"required":["isbn"]}"""),
        ],
        "You are the assistant of a bookshop.",
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
        var api = new FakeApi().Recorded("thinking-text.sse").Stream(Sse.Text());
        using var model = Model(api);
        var agent = new AgentDefinition { Model = model, Instructions = "Answer briefly." };
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "Find Gaudy Night.", cancellationToken: Ct);
        var stored = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        await agent.RunAsync(stored, "Thanks.", cancellationToken: Ct);

        var replayed = conversation.Messages[^1].Blocks;
        Assert.Equal(2, replayed.Length);
        Assert.Contains("[" + string.Join(",", replayed.Select(block => block.Raw)) + "]", api.Requests[1], StringComparison.Ordinal);
    }

    private static string ReadRaw(string name) =>
        JsonDocument.Parse(Golden("blocks.json")).RootElement.GetProperty(name).GetString()!;

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recordings", name)).ReplaceLineEndings("\n");
}
