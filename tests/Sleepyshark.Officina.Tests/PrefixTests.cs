using System.Collections.Immutable;
using System.Text.Json;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

public class PrefixTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Changes => ["instructions", "tool description", "tool schema", "added tool", "model settings"];

    [Theory]
    [MemberData(nameof(Changes))]
    public async Task A_changed_prefix_fails_the_run_with_a_prefix_mismatch_before_any_model_call(string change)
    {
        var conversation = new Conversation();
        await Agents.With(new ScriptedModel().Reply("Hi."), tools: Agents.SearchTool()).RunAsync(conversation, "Hi", cancellationToken: Ct);
        var saved = conversation.Messages;
        var model = new ScriptedModel { Settings = change == "model settings" ? "scripted, effort high" : "scripted" }.Reply("Unused.");
        var changed = change switch
        {
            "instructions" => Agents.With(model, "You are a terse assistant.", Agents.SearchTool()),
            "tool description" => Agents.With(model, tools: Agents.SearchTool("Finds books.")),
            "tool schema" => Agents.With(model, tools: Agents.Tool("search", "Searches the catalogue.")),
            "added tool" => Agents.With(model, tools: [Agents.SearchTool(), Agents.Tool("order", "Places an order.")]),
            _ => Agents.With(model, tools: Agents.SearchTool()),
        };

        var result = await changed.RunAsync(conversation, "Again", cancellationToken: Ct);

        Assert.Equal(FailureReason.PrefixMismatch, Assert.IsType<Failed>(result).Reason);
        Assert.Empty(model.Requests);
        Assert.Equal(saved, conversation.Messages);
    }

    [Fact]
    public async Task Tools_given_in_another_order_are_the_same_prefix()
    {
        Tool[] tools = [Agents.SearchTool(), Agents.Tool("order", "Places an order.")];
        var model = new ScriptedModel().Reply("One.").Reply("Two.");
        var conversation = new Conversation();

        await Agents.With(model, tools: tools).RunAsync(conversation, "Hi", cancellationToken: Ct);
        var result = await Agents.With(model, tools: [.. tools.Reverse()]).RunAsync(conversation, "Again", cancellationToken: Ct);

        Assert.IsType<Completed>(result);
        Assert.All(model.Requests, request => Assert.Equal(["order", "search"], request.Tools.Select(tool => tool.Name)));
    }

    [Fact]
    public async Task The_prefix_is_stable_across_turns_and_across_save_restart_and_resume()
    {
        // Before the restart: two turns, with run context, and a reasoning block kept as raw JSON.
        var before = new ScriptedModel()
            .Reply(
                new BlockReceived(new ContentBlock(null, """{ "type": "thinking", "thinking": "caf\u00e9", "signature": "c2ln" }""")),
                new BlockReceived(ScriptedModel.TextBlock("Hello.")),
                new ModelStopped(ModelStopReason.End))
            .Reply("Paris.");
        var conversation = new Conversation();
        var agent = Agents.With(before, tools: Agents.SearchTool());
        await agent.RunAsync(conversation, "Hi", "Date: 2026-10-05.", Ct);
        await agent.RunAsync(conversation, "Capital of France?", cancellationToken: Ct);
        var saved = JsonSerializer.Serialize(conversation);

        // After it: only the JSON is left, and the definition is built afresh.
        var after = new ScriptedModel().Reply("Berlin.").Reply("Rome.");
        var resumed = JsonSerializer.Deserialize<Conversation>(saved)!;
        var rebuilt = Agents.With(after, tools: Agents.SearchTool());
        await rebuilt.RunAsync(resumed, "And Germany?", "Date: 2026-10-06.", Ct);
        var last = await rebuilt.RunAsync(resumed, "And Italy?", cancellationToken: Ct);

        Assert.Equal("Rome.", Assert.IsType<Completed>(last).Text);
        Assert.Empty(PrefixStability.Problems([.. before.Requests, .. after.Requests]));
    }

    [Fact]
    public void The_stability_check_reports_each_kind_of_change()
    {
        var hi = Message.Of(Role.User, "Hi");
        var hello = new Message(Role.Assistant, [ScriptedModel.TextBlock("Hello.")]);
        var first = new ModelRequest([Agents.SearchTool()], "A", [hi]);
        ModelRequest[] requests =
        [
            first,
            first with { Messages = [hi, hello] },
            first with { Tools = [Agents.SearchTool("Changed.")], Messages = [hi, hello] },
            first with { Tools = [Agents.SearchTool("Changed.")], Instructions = "B", Messages = [hi, hello] },
            first with { Tools = [Agents.SearchTool("Changed.")], Instructions = "B", Messages = [hi, new Message(Role.Assistant, [ScriptedModel.TextBlock("Hi.")])] },
            first with { Tools = [Agents.SearchTool("Changed.")], Instructions = "B", Messages = ImmutableArray<Message>.Empty },
            first with { Tools = [Agents.SearchTool("Changed.")], Instructions = "B", Messages = ImmutableArray<Message>.Empty, OutputSchema = """{"type":"string"}""" },
        ];

        Assert.Equal(
            [
                "Request 3: the tools differ from request 2's.",
                "Request 4: the instructions differ from request 3's.",
                "Request 5: message 2 differs from request 4's.",
                "Request 6: has 0 messages, fewer than request 5's 2.",
                "Request 7: the output schema differs from request 6's.",
            ],
            PrefixStability.Problems(requests));
    }
}
