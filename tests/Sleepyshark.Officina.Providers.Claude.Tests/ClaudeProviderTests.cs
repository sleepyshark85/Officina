using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;
using static Sleepyshark.Officina.Providers.Claude.Tests.Recordings;

namespace Sleepyshark.Officina.Providers.Claude.Tests;

/// <summary>
/// The mapping between the core and the Claude API, checked offline against golden JSON (CLD-02): the request the provider
/// sends must equal the recorded one, and the recorded reply must read as the expected events.
/// </summary>
public sealed class ClaudeProviderTests : IDisposable
{
    private static readonly ModelProfile Profile = new() { Model = "claude-opus-5-5" };

    private static readonly ToolDefinition Search = new("search", null, null, "web_search");

    private readonly string temporary = Path.GetTempFileName();

    public void Dispose() => File.Delete(temporary);

    // CLD-01, CLD-03, CLD-05, CLD-07, CLD-09, TOOL-13: the prefix with project memory after the instructions, cache markers with
    // their lifetimes, a turn-scoped volatile context, an operator's system message, reasoning sent back unchanged, provider
    // limits, and the streamed reply.
    [Fact]
    public async Task A_request_and_its_streamed_reply_map_as_recorded()
    {
        var schema = Json("""{ "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"], "additionalProperties": false }""");
        var request = new ModelRequest(
            Profile with { Effort = "high", MaxOutputTokens = 2000, Settings = new Dictionary<string, string> { ["thinking"] = """{ "type": "adaptive", "display": "summarized" }""" } },
            [new("read_file", "Reads a file.", schema, null), Search with { Limits = new() { MaxUses = 3, AllowedDomains = ["learn.microsoft.com"] } }],
            "Fix bugs.",
            [
                Message.User("Fix a.cs."),
                new(Role.Assistant, [
                    new ReasoningContent("Read it first.", "sig-1"), new ProviderContent(Json("""{ "type": "redacted_thinking", "data": "opaque" }""")),
                    new TextContent("Reading."), new ToolUseContent("toolu_1", "read_file", Json("""{ "path": "a.cs" }""")),
                ]),
                new(Role.User, [new ToolResultContent("toolu_1", "class A {}", isError: false)]),
                Message.System("Keep it short."),
                new(Role.System, [new TextContent("Today is 2026-10-02.")], turnScoped: true),
            ],
            [new(CachePoint.Instructions, TimeSpan.FromHours(1)), new(CachePoint.Memory, TimeSpan.FromHours(1)), new(CachePoint.History, TimeSpan.FromMinutes(5))])
        {
            Memory = "<project-memory>\n- note #1 build: Run dotnet test.\n</project-memory>",
        };
        using var provider = Replay(Named("mapping.json"));

        var events = await StreamAsync(provider, request);

        Assert.Equal(
            [
                new ContentReceived(new ReasoningContent("The bug is in A.", "sig-2")), new TextDelta("Fixing"), new TextDelta(" it."),
                new ContentReceived(new ToolUseContent("toolu_2", "read_file", Json("""{ "path": "b.cs" }"""))),
                new UsageReported(new Usage(12, 42, 2048, 300, cacheWrite1h: 200)), new Stopped(StopReason.WantsTools),
            ],
            events);
    }

    // TOOL-13, MSG-02: a server tool's call and result are kept as Claude sent them, and reported under the configured name.
    [Fact]
    public async Task A_server_tool_s_call_and_result_are_kept_and_reported_as_a_provider_tool_call()
    {
        using var provider = Replay(Named("server-tool.json"));

        var events = await StreamAsync(provider, new ModelRequest(Profile, [Search], "Research.", [Message.User("Find the SDK.")], []));

        var result = """[{ "type": "web_search_result", "url": "https://github.com/anthropics/anthropic-sdk-csharp", "title": "anthropic-sdk-csharp", "encrypted_content": "enc", "page_age": null }]""";
        Assert.Equal(
            [
                new ContentReceived(new ProviderContent(Json("""{ "type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": { "query": "anthropic csharp sdk" } }"""))),
                new ContentReceived(new ProviderContent(Json($$"""{ "type": "web_search_tool_result", "tool_use_id": "srvtoolu_1", "content": {{result}} }"""))),
                new TextDelta("Found it."), new UsageReported(new Usage(900, 30, 0, 0)), new Stopped(StopReason.Finished),
            ],
            events.Where(modelEvent => modelEvent is not ProviderToolUsed));
        var used = Assert.Single(events.OfType<ProviderToolUsed>());
        Assert.Equal("search", used.Request.Name);
        Assert.True(JsonElement.DeepEquals(Json("""{ "query": "anthropic csharp sdk" }"""), used.Request.Arguments));
        Assert.True(JsonElement.DeepEquals(Json(result), Json(used.Result)));
    }

    // TOOL-13: a paused reply can end with a server tool's call, whose result arrives in the reply that continues it.
    [Fact]
    public async Task A_server_tool_s_result_in_the_reply_after_a_pause_is_reported_once()
    {
        using var provider = Replay(Named("pause-turn.json"));
        var first = await StreamAsync(provider, new ModelRequest(Profile, [Search], "Research.", [Message.User("Find the SDK.")], []));
        var paused = first.OfType<ContentReceived>().Select(received => received.Content).ToArray();
        Assert.Empty(first.OfType<ProviderToolUsed>());
        Assert.Contains(new Stopped(StopReason.Paused), first);

        var second = await StreamAsync(
            provider, new ModelRequest(Profile, [Search], "Research.", [Message.User("Find the SDK."), new Message(Role.Assistant, paused)], []));

        var used = Assert.Single(second.OfType<ProviderToolUsed>());
        Assert.Equal("search", used.Request.Name);
        Assert.True(JsonElement.DeepEquals(Json("""{ "query": "anthropic csharp sdk" }"""), used.Request.Arguments));
    }

    // CLD-04, TEST-03.
    [Theory]
    [InlineData("end_turn", StopReason.Finished)]
    [InlineData("tool_use", StopReason.WantsTools)]
    [InlineData("max_tokens", StopReason.OutputLimit)]
    [InlineData("stop_sequence", StopReason.StopSequence)]
    [InlineData("pause_turn", StopReason.Paused)]
    [InlineData("refusal", StopReason.Refused)]
    [InlineData("model_context_window_exceeded", StopReason.InputTooLong)]
    [InlineData("a_reason_from_the_future", StopReason.Unknown)]
    public async Task Every_stop_reason_is_recognised_and_any_other_is_unknown(string reason, StopReason expected)
    {
        var events = await AnswerAsync(200, $$"""[{{Start}}, { "type": "message_delta", "delta": { "stop_reason": "{{reason}}" }, "usage": { "output_tokens": 1 } }, { "type": "message_stop" }]""");

        Assert.Equal(new Stopped(expected), events[^1]);
    }

    // CLD-08, DESIGN.md §9.
    [Theory]
    [InlineData(400, "invalid_request_error", ModelFailure.InvalidRequest)]
    [InlineData(401, "authentication_error", ModelFailure.Authentication)]
    [InlineData(403, "permission_error", ModelFailure.Authentication)]
    [InlineData(404, "not_found_error", ModelFailure.InvalidRequest)]
    [InlineData(429, "rate_limit_error", ModelFailure.RateLimited)]
    [InlineData(500, "api_error", ModelFailure.Transient)]
    [InlineData(529, "overloaded_error", ModelFailure.Transient)]
    public async Task Errors_are_classified(int status, string type, ModelFailure expected)
    {
        var failure = await Assert.ThrowsAsync<ModelCallException>(() => AnswerAsync(status, Error(type, "Something went wrong.")));

        Assert.Equal(expected, failure.Failure);
    }

    // CLD-08: an error after the stream started has no status, only its type.
    [Fact]
    public async Task An_overload_mid_stream_is_transient()
    {
        var failure = await Assert.ThrowsAsync<ModelCallException>(() => AnswerAsync(200, $"[{Start}, {Error("overloaded_error", "Overloaded")}]"));

        Assert.Equal(ModelFailure.Transient, failure.Failure);
    }

    // CLD-08, HIST-04: so the turn shortens the history.
    [Fact]
    public async Task A_prompt_that_is_too_long_stops_for_input_too_long()
    {
        var events = await AnswerAsync(400, Error("invalid_request_error", "prompt is too long: 1000001 tokens > 1000000 maximum"));

        Assert.Equal([new Stopped(StopReason.InputTooLong)], events);
    }

    private const string Start = """
        { "type": "message_start", "message": { "id": "msg", "type": "message", "role": "assistant", "model": "claude-opus-5-5", "content": [],
          "stop_reason": null, "stop_sequence": null, "usage": { "input_tokens": 1, "output_tokens": 1 } } }
        """;

    private static string Error(string type, string message) => $$"""{ "type": "error", "error": { "type": "{{type}}", "message": "{{message}}" } }""";

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Streams a request answered with this status and response, whatever the request is.</summary>
    private async Task<List<ModelEvent>> AnswerAsync(int status, string response)
    {
        await File.WriteAllTextAsync(temporary, $$"""[{ "status": {{status}}, "response": {{response}} }]""", TestContext.Current.CancellationToken);
        using var provider = Replay(temporary);
        return await StreamAsync(provider, new ModelRequest(Profile, [], "Answer.", [Message.User("Hi.")], []));
    }
}
