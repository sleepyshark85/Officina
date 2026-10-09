using System.Text.Json;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>
/// A prefix, and a session that crashed while its tools ran, shared with the Go implementation in the repository's
/// testdata/session: each implementation checks that it writes its own session file byte for byte as stored, and
/// resumes the other's, which neither rewrites. The prefix's strings hold every kind of character JSON escapes, and a
/// schema is written with odd spacing and an escape, as the fingerprint takes it as given.
/// </summary>
public class SharedSessionTests
{
    private const string Settings = "claude model=claude-opus-5-5 effort=medium max_tokens=16000 cache=1h thinking=adaptive";

    private static readonly string Escapes = string.Concat(
        "\"quoted\" back\\slash /slash tab\tnew\nline cr\rff\fbs\b nul", (char)0, " unit", (char)0x1F, " del", (char)0x7F,
        " <b>&amp; 'apos' +1 `tick` caf", (char)0xE9, " ", (char)0x2603, " ", char.ConvertFromUtf32(0x1F600), " line", (char)0x2028,
        "sep nbsp", (char)0xA0, ".");

    // A backslash, then u00e9: an escape the schema and the block keep as written.
    private static readonly string Escaped = "\\" + "u00e9";

    private static readonly string SearchSchema =
        "{ \"type\" : \"object\", \"description\": \"caf" + Escaped + " \\/ <q>\", \"properties\": { \"query\": { \"type\": \"string\" } }, \"required\": [ \"query\" ] }";

    private const string OrderSchema =
        """{"type":"object","properties":{"bookId":{"type":"integer"},"quantity":{"type":"integer","minimum":1}},"required":["bookId","quantity"],"additionalProperties":false}""";

    private static readonly string Thinking =
        "{ \"type\":\"thinking\",  \"thinking\":\"caf" + Escaped + " \\\"quoted\\\" \\/ " + (char)0xE9 + "\", \"signature\":\"c2ln+/=\" }";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).TrimEnd('\r', '\n');

    private static Agent Shared(ScriptedModel model) => Agents.With(
        model,
        "You are the shared test assistant. " + Escapes,
        Agents.Tool("search", "Searches the catalogue. " + Escapes, SearchSchema),
        Agents.Tool("place_order", "Places an order «now».", OrderSchema, ToolKind.Write));

    [Fact]
    public async Task The_prefix_fingerprint_and_a_crashed_session_are_written_as_the_shared_fixtures_hold_them()
    {
        var model = new ScriptedModel { Settings = Settings }
            .Reply(
                new BlockReceived(new ContentBlock(null, Thinking)),
                new BlockReceived(ScriptedModel.TextBlock("Looking up «Café Libro» & <friends>.")),
                new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall("toolu_01", "search", """{"query":"café"}"""))),
                new ModelStopped(ModelStopReason.ToolUse))
            .Reply("It is in stock: 3 copies.")
            .CallTools(new ToolCall("toolu_02", "place_order", """{"bookId":7,"quantity":1}"""));
        var agent = Shared(model);
        var conversation = new Conversation { Id = "dotnet-0001" };
        await agent.RunAsync(conversation, "Is Café Libro in stock?", new() { Context = "Today is Friday 9 October 2026. The staff member is Zoë." }, Ct);

        // The application stops while the order's tool runs: what it saved after the reply that asked for it is all there is.
        string? saved = null;
        await foreach (var runEvent in agent.StreamAsync(conversation, "Order one copy for <Ann & Bob>.", cancellationToken: Ct))
        {
            if (runEvent is ConversationAppended { Message.Role: Role.Assistant })
            {
                saved = JsonSerializer.Serialize(conversation);
                break;
            }
        }

        var prefix = agent.Prefix();
        var shared = JsonSerializer.Deserialize<JsonElement>(Fixture("prefix.json"));
        Assert.Equal(shared.GetProperty("fingerprint").GetString(), prefix.Fingerprint);
        Assert.Equal(shared.GetProperty("settings").GetString(), prefix.ModelSettings);
        Assert.Equal(shared.GetProperty("instructions").GetString(), prefix.Instructions);
        Assert.Equal(
            shared.GetProperty("tools").EnumerateArray().Select(tool => (tool.GetProperty("name").GetString(), tool.GetProperty("description").GetString(), tool.GetProperty("inputSchema").GetString())),
            prefix.Tools.Select(tool => ((string?)tool.Name, (string?)tool.Description, (string?)tool.InputSchema)));
        Assert.Equal(Fixture("dotnet-session.json"), saved);
    }

    [Fact]
    public async Task A_session_the_Go_implementation_saved_mid_reply_resumes_with_its_prefix_and_interrupted_calls_answered()
    {
        var stored = Fixture("go-session.json");
        var conversation = JsonSerializer.Deserialize<Conversation>(stored)!;
        var model = new ScriptedModel { Settings = Settings }.Reply("The order may or may not have been placed; let me check.");
        var agent = Shared(model);

        Assert.True(agent.CanContinue(conversation));
        var result = await agent.RunAsync(conversation, "Did the order go through?", cancellationToken: Ct);

        Assert.IsType<Completed>(result);
        var messages = Assert.Single(model.Requests).Messages;
        Assert.Equal(JsonSerializer.Deserialize<Conversation>(stored)!.Messages, messages[..^2]);
        var interrupted = Assert.Single(messages[^2].Blocks).ToolResult!;
        Assert.Equal(("go_02", true), (interrupted.CallId, interrupted.IsError));
        Assert.Contains("interrupted", interrupted.Content, StringComparison.Ordinal);
        Assert.Null(RoleSequence.Problem(messages));
    }
}
