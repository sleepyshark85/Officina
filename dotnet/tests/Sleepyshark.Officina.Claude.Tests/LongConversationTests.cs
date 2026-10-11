using System.Text.Json;
using static Sleepyshark.Officina.Claude.Tests.StreamTests;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>Server-side compaction and tool-result clearing, with the response shapes recorded live.</summary>
public class LongConversationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ContextManagement Both = new() { CompactAt = 50_000, ClearToolResults = new ToolResultClearing(After: 3, Keep: 1, AtLeastTokens: 5_000) };

    [Fact]
    public void Claude_declares_compaction_and_context_editing()
    {
        using var model = Model(new FakeApi());

        Assert.Equal(ModelCapabilities.Compaction | ModelCapabilities.ContextEditing, ((IModel)model).Capabilities);
    }

    [Fact]
    public void Context_management_asks_for_clearing_then_threshold_compaction_with_their_betas()
    {
        using var model = Model(new FakeApi());

        var built = ClaudeRequest.Build(model, Hi with { Prefix = Hi.Prefix with { ContextManagement = Both } });

        Assert.Equal(
            """{"edits":[{"type":"clear_tool_uses_20250919","trigger":{"type":"tool_uses","value":3},"keep":{"type":"tool_uses","value":1},"clear_at_least":{"type":"input_tokens","value":5000}},"""
            + """{"type":"compact_20260112","trigger":{"type":"input_tokens","value":50000}}]}""",
            JsonSerializer.Serialize(built.RawBodyData["context_management"]));
        Assert.Equal(["context-management-2025-06-27", "compact-2026-01-12"], built.Betas!.Select(beta => beta.Raw()));
    }

    [Fact]
    public void Without_context_management_the_request_has_none_and_no_betas()
    {
        using var model = Model(new FakeApi());

        var built = ClaudeRequest.Build(model, Hi with { Prefix = Hi.Prefix with { ContextManagement = new ContextManagement { CompactAt = 60_000 } } });
        var plain = ClaudeRequest.Build(model, Hi);
        var empty = ClaudeRequest.Build(model, Hi with { Prefix = Hi.Prefix with { ContextManagement = new ContextManagement() } });

        Assert.Equal(["compact-2026-01-12"], built.Betas!.Select(beta => beta.Raw()));
        Assert.All([plain, empty], request => Assert.DoesNotContain("context_management", request.RawBodyData.Keys));
        Assert.All([plain, empty], request => Assert.Null(request.Betas));
    }

    [Fact]
    public async Task A_compaction_is_reported_from_its_iteration_and_priced_with_the_reply()
    {
        using var model = Model(new FakeApi().Fixture("compaction-iterations.sse"));

        var events = await CollectAsync(model, Hi);

        // The compaction iteration read 48 + 2,615 cached + 50,090 written tokens, and wrote a 578-token summary.
        Assert.Equal(new CompactionReported(52_753, 578), Assert.Single(events.OfType<CompactionReported>()));
        var usage = Assert.Single(events.OfType<UsageReceived>()).Usage;
        Assert.Equal(new Usage(50, 663, 5230, 50648), usage);
        Assert.Equal(((50 * 4m) + (663 * 20m) + (5230 * 0.20m) + (50648 * 5m)) / 1_000_000m, model.Price!.Cost(usage));
    }

    [Fact]
    public async Task A_clearing_is_reported_from_the_applied_edits()
    {
        using var model = Model(new FakeApi().Fixture("clearing.sse"));

        var events = await CollectAsync(model, Hi);

        Assert.Equal(new ClearingReported(4_892, 2), Assert.Single(events.OfType<ClearingReported>()));
        Assert.Empty(events.OfType<CompactionReported>());
        Assert.Equal(new ModelStopped(ModelStopReason.ToolUse), events[^1]);
    }

    [Fact]
    public async Task The_compaction_block_is_kept_and_replayed_as_received_and_the_run_reports_it()
    {
        var api = new FakeApi().Fixture("compaction-iterations.sse").Stream(Sse.Text());
        using var model = Model(api);
        var agent = new Agent { Model = model, Instructions = "Answer briefly.", ContextManagement = Both };
        var conversation = new Conversation();

        var events = new List<RunEvent>();
        await foreach (var runEvent in agent.StreamAsync(conversation, "Which shelf?", cancellationToken: Ct))
        {
            events.Add(runEvent);
        }

        var stored = JsonSerializer.Deserialize<Conversation>(JsonSerializer.Serialize(conversation))!;
        await agent.RunAsync(stored, "Thanks.", cancellationToken: Ct);

        Assert.Equal(new ConversationCompacted(52_753, 578), Assert.Single(events.OfType<ConversationCompacted>()));
        var compaction = conversation.Messages[^1].Blocks[0];
        Assert.Equal("""{"type":"compaction","content":"Summary: the customer asked about shelf Q2."}""", compaction.Raw);
        Assert.Contains("[" + string.Join(",", conversation.Messages[^1].Blocks.Select(block => block.Raw)) + "]", api.Requests[1], StringComparison.Ordinal);
        Assert.All(api.Requests, request => Assert.Contains("\"compact_20260112\"", request, StringComparison.Ordinal));
    }
}
