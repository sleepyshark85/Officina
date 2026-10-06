using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>Long conversations (HIST-01…04): the provider shortens them on its side, and the run reports what it did.</summary>
public class LongConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ContextManagement Both = new() { CompactAt = 50_000, ClearToolResults = new ToolResultClearing(After: 3, Keep: 1) };

    private static readonly ContentBlock Summary = new(null, """{"type":"compaction","content":"The customer asked about shelf Q2."}""");

    private static ScriptedModel Capable() => new() { Capabilities = ModelCapabilities.Compaction | ModelCapabilities.ContextEditing };

    [Fact]
    public async Task A_model_without_compaction_says_so_and_an_agent_that_needs_it_cannot_run_on_it()
    {
        var model = new ScriptedModel().Reply("Hi.");

        Assert.Equal(ModelCapabilities.None, ((IModel)model).Capabilities);
        var compacting = Agents.With(model) with { ContextManagement = new ContextManagement { CompactAt = 50_000 } };
        var clearing = Agents.With(model) with { ContextManagement = new ContextManagement { ClearToolResults = new ToolResultClearing(3, 1) } };
        Assert.Throws<InvalidOperationException>(() => compacting.StreamAsync(new Conversation(), "Hi.", cancellationToken: Ct));
        Assert.Throws<InvalidOperationException>(() => clearing.StreamAsync(new Conversation(), "Hi.", cancellationToken: Ct));
        Assert.IsType<Completed>(await (Agents.With(model) with { ContextManagement = new ContextManagement() }).RunAsync(new Conversation(), "Hi.", cancellationToken: Ct));
    }

    [Fact]
    public async Task Without_compaction_a_full_window_stops_the_run_as_context_full_and_leaves_the_conversation_as_it_was()
    {
        var model = new ScriptedModel().Reply("Hi.").Reply(new ModelStopped(ModelStopReason.ContextFull));
        var agent = Agents.With(model);
        var conversation = new Conversation();
        await agent.RunAsync(conversation, "Hi.", cancellationToken: Ct);

        var result = await agent.RunAsync(conversation, "And now?", cancellationToken: Ct);

        Assert.Equal(new Stopped(StopReason.ContextFull, null, default), Agents.Outcome(result));
        Assert.Equal(2, conversation.Messages.Length);
    }

    [Fact]
    public async Task Compaction_and_clearing_are_events_audit_entries_and_telemetry_and_the_summary_is_kept()
    {
        using var telemetry = new TelemetryCollector();
        var sink = new RecordingSink();
        var model = Capable().Reply(
            new BlockReceived(Summary), new TextDelta("Shelf Q2."), new BlockReceived(ScriptedModel.TextBlock("Shelf Q2.")),
            new CompactionReported(52_753, 578), new ClearingReported(4_892, 2), new ModelStopped(ModelStopReason.End));
        var agent = Agents.With(model) with { Name = $"agent-{Guid.NewGuid():N}", AuditSink = sink, ContextManagement = Both };
        var conversation = new Conversation();

        var events = await Agents.CollectAsync(agent.StreamAsync(conversation, "Which shelf?", cancellationToken: Ct));

        Assert.Equal(Both, Assert.Single(model.Requests).Prefix.ContextManagement);
        Assert.Equal(
            [new ConversationCompacted(52_753, 578), new ToolResultsCleared(4_892, 2)],
            events.Where(runEvent => runEvent is ConversationCompacted or ToolResultsCleared));
        Assert.Same(Summary, conversation.Messages[^1].Blocks[0]);
        Assert.Equal(
            [(AuditKind.Compacted, "52,753 tokens summarized into 578."), (AuditKind.Cleared, "Results of 2 tool calls cleared: 4,892 tokens.")],
            sink.Entries.Where(entry => entry.Kind is AuditKind.Compacted or AuditKind.Cleared).Select(entry => (entry.Kind, entry.Detail)));
        var call = Assert.Single(telemetry.Spans(agent.Name), span => span.OperationName == "chat scripted");
        Assert.Equal(52_753L, call.GetTagItem("officina.compaction.tokens"));
        Assert.Equal(578L, call.GetTagItem("officina.compaction.summary_tokens"));
        Assert.Equal(4_892L, call.GetTagItem("officina.clearing.tokens"));
        Assert.Equal(2, call.GetTagItem("officina.clearing.tool_calls"));
        Assert.Equal(
            ["officina.model.clearings", "officina.model.compactions"],
            telemetry.Measurements(agent.Name).Select(measured => measured.Instrument).Where(name => name is "officina.model.compactions" or "officina.model.clearings").Order());
    }

    [Fact]
    public void Context_management_is_part_of_the_prefix()
    {
        var model = Capable();
        var conversation = new Conversation();
        conversation.Bind((Agents.With(model) with { ContextManagement = Both }).Prefix().Fingerprint);

        Assert.True((Agents.With(model) with { ContextManagement = Both with { } }).CanContinue(conversation));
        Assert.False((Agents.With(model) with { ContextManagement = Both with { CompactAt = 60_000 } }).CanContinue(conversation));
        Assert.False((Agents.With(model) with { ContextManagement = Both with { ClearToolResults = new ToolResultClearing(3, 2) } }).CanContinue(conversation));
        Assert.False(Agents.With(model).CanContinue(conversation));
    }

    /// <summary>
    /// Pins the fingerprint of an agent with neither typed output nor context management: the SHA-256 of
    /// <c>{"model":…,"instructions":…,"tools":[…]}</c>. A change to it stops every stored session from resuming (CTX-04).
    /// </summary>
    [Fact]
    public void The_fingerprint_of_a_plain_agent_does_not_change()
    {
        var agent = Agents.With(new ScriptedModel(), tools: Agents.SearchTool());

        Assert.Equal("b4e28e3b1737858911d2b39b69bef5f10afe9dc44e2351e44211a6c874ba9279", agent.Prefix().Fingerprint);
    }

    [Fact]
    public void An_empty_setting_leaves_the_prefix_as_no_setting_does()
    {
        var model = Capable();

        Assert.Equal(Agents.With(model).Prefix().Fingerprint, (Agents.With(model) with { ContextManagement = new ContextManagement() }).Prefix().Fingerprint);
    }

    [Fact]
    public void Thresholds_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContextManagement { CompactAt = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultClearing(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultClearing(3, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolResultClearing(3, 1, -5));
    }
}
