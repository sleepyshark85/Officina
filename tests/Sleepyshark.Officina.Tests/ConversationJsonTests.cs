using System.Text.Json;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

public class ConversationJsonTests
{
    // Odd spacing, key order and escapes, as a provider might send them; none of it may change.
    private const string Thinking = """{ "type":"thinking",  "thinking":"café \"quoted\" \/ é", "signature":"c2ln+/=" }""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_conversation_round_trips_through_JSON_keeping_every_block_byte_for_byte(bool indented)
    {
        var model = new ScriptedModel().Reply(
            new BlockReceived(new ContentBlock(null, Thinking)),
            new BlockReceived(new ContentBlock("Bonjour ☃", """{"type":"text","text":"Bonjour ☃"}""")),
            new ModelStopped(ModelStopReason.End));
        var conversation = new Conversation();
        await Agents.With(model).RunAsync(conversation, "Salut ✓", "Date: 2026-10-05.", TestContext.Current.CancellationToken);
        var options = new JsonSerializerOptions { WriteIndented = indented };

        var json = JsonSerializer.Serialize(conversation, options);
        var restored = JsonSerializer.Deserialize<Conversation>(json, options)!;

        Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        Assert.Equal(conversation.Fingerprint, restored.Fingerprint);
        Assert.Equal(conversation.Messages, restored.Messages);
        Assert.Equal(Thinking, restored.Messages[^1].Blocks[0].Raw);
        Assert.Equal([Role.User, Role.Operator, Role.Assistant], restored.Messages.Select(message => message.Role));
    }

    [Fact]
    public void The_JSON_form_is_plain_and_readable()
    {
        var conversation = JsonSerializer.Deserialize<Conversation>(
            """{"fingerprint":"abc","messages":[{"role":"user","blocks":[{"text":"Hi"}]},{"role":"assistant","blocks":[{"raw":{"type":"x"}}]}]}""")!;

        Assert.Equal("abc", conversation.Fingerprint);
        Assert.Equal("Hi", conversation.Messages[0].Text);
        Assert.Equal("""{"type":"x"}""", conversation.Messages[1].Blocks[0].Raw);
        Assert.Equal(
            """{"fingerprint":"abc","messages":[{"role":"user","blocks":[{"text":"Hi"}]},{"role":"assistant","blocks":[{"raw":{"type":"x"}}]}]}""",
            JsonSerializer.Serialize(conversation));
    }

    [Fact]
    public void A_block_needs_valid_raw_JSON_and_a_message_needs_a_block()
    {
        Assert.ThrowsAny<JsonException>(() => new ContentBlock(null, "{not json"));
        Assert.Throws<ArgumentException>(() => new ContentBlock(null, null));
        Assert.Throws<ArgumentException>(() => new Message(Role.User, []));
    }
}
