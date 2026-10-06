using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Samples.Chat;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;

namespace Samples.Tests;

/// <summary>GEN-06: the chat assistant sample, offline, with only the model, the clock and storage replaced.</summary>
public class ChatAssistantTests
{
    private readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero));
    private readonly Dictionary<string, string> conversations = [];
    private readonly InMemoryMemoryStore memory = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ToolCall Call(string id, string name, object input) => new(id, name, JsonSerializer.Serialize(input));

    private static ToolResult LastResult(ScriptedModel model) => model.Requests[^1].Messages[^1].Blocks[0].ToolResult!;

    private ChatAssistant Assistant(ScriptedModel model) => new(ChatAssistant.Create(model, memory), conversations, time);

    [Fact]
    public async Task TEST_02_a_user_s_conversation_goes_on_after_a_restart_with_the_same_prefix_and_its_tool_answers()
    {
        var before = new ScriptedModel()
            .CallTools(Call("t1", "celsius_to_fahrenheit", new { celsius = 20 }))
            .Reply("20 °C is 68 °F.");
        var first = await Assistant(before).ReplyAsync("ana", "What is 20 °C in Fahrenheit?", Ct);

        // A restart: only the stored JSON is left, and the agent is built afresh.
        time.Advance(TimeSpan.FromDays(1));
        var after = new ScriptedModel()
            .CallTools(Call("t2", "celsius_to_fahrenheit", new { celsius = 30 }))
            .Reply("30 °C is 86 °F.");
        var second = await Assistant(after).ReplyAsync("ana", "And 30?", Ct);

        Assert.Equal("20 °C is 68 °F.", Assert.IsType<Completed>(first).Text);
        Assert.Equal("30 °C is 86 °F.", Assert.IsType<Completed>(second).Text);
        Assert.Equal(new ToolResult("t2", "86", false), LastResult(after));
        Assert.Empty(PrefixStability.Problems([.. before.Requests, .. after.Requests]));

        // The date came as run context each day, after the user's message.
        var sent = after.Requests[^1].Messages;
        Assert.Equal(
            ["Today is 2026-10-06.", "Today is 2026-10-07."],
            sent.Where(message => message.Role == Role.Operator).Select(message => message.Text));
        Assert.Equal(["ana"], conversations.Keys);
    }

    [Fact]
    public async Task A_conversation_of_a_changed_agent_is_started_anew_rather_than_failing()
    {
        await Assistant(new ScriptedModel().Reply("Hello.")).ReplyAsync("ana", "Hi.", Ct);

        // An upgrade changed the model settings, which are part of the prefix (CTX-04).
        var upgraded = new ScriptedModel { Settings = "scripted, effort high" }.Reply("Hello again.");
        var result = await Assistant(upgraded).ReplyAsync("ana", "Hi again.", Ct);

        Assert.Equal("Hello again.", Assert.IsType<Completed>(result).Text);
        Assert.Equal(["Hi again.", "Today is 2026-10-06."], upgraded.Requests[0].Messages.Select(message => message.Text));
        Assert.DoesNotContain("Hi.", conversations["ana"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Memory_is_kept_per_user_across_conversations_and_one_user_never_sees_another_s()
    {
        var model = new ScriptedModel()
            .CallTools(Call("m1", MemoryTool.Name, new { command = "create", path = "/memories/preferences.md", file_text = "Units: Fahrenheit.\n" }))
            .Reply("Noted: Fahrenheit from now on.")
            .CallTools(Call("m2", MemoryTool.Name, new { command = "view", path = "/memories" }))
            .Reply("I don't remember anything about you yet.");
        var assistant = Assistant(model);

        await assistant.ReplyAsync("ana", "I prefer Fahrenheit.", Ct);
        var ben = await assistant.ReplyAsync("ben", "What do you remember about me?", Ct);

        Assert.IsType<Completed>(ben);
        Assert.EndsWith("\n0B\t/memories", LastResult(model).Content, StringComparison.Ordinal);

        // A new conversation for Ana, as when the host forgets it, still finds her preference in memory.
        conversations.Clear();
        var later = new ScriptedModel()
            .CallTools(Call("m3", MemoryTool.Name, new { command = "view", path = "/memories/preferences.md" }))
            .Reply("It's 68 °F.");
        await Assistant(later).ReplyAsync("ana", "What's 20 °C?", Ct);

        Assert.Contains("Units: Fahrenheit.", LastResult(later).Content, StringComparison.Ordinal);
        Assert.Equal(["preferences.md"], (await memory.ListAsync("ana", Ct)).Select(file => file.Path));
        Assert.Empty(await memory.ListAsync("ben", Ct));

        // Memory is a tool the model calls, never part of the instructions (MEM-05).
        Assert.DoesNotContain("Fahrenheit.", later.Requests[0].Prefix.Instructions, StringComparison.Ordinal);
    }
}
