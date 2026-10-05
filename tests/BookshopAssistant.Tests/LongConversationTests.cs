using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>APP-17 and HIST-04 in the console (TEST-09); the live part of APP-17 is the demo script's.</summary>
public class LongConversationTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    [DatabaseFact]
    public async Task APP_17_demo_mode_compacts_and_clears_early_and_the_console_and_audit_report_each()
    {
        var model = Model()
            .Reply(SayThenCall("Searching.", Call("c1", "search_books", new { title = "Winter" })))
            .Reply(
                new BlockReceived(new ContentBlock(null, """{"type":"compaction","content":"Sam searched the catalogue."}""")),
                new TextDelta("Found it."), new BlockReceived(ScriptedModel.TextBlock("Found it.")),
                new CompactionReported(52_753, 578), new ClearingReported(4_892, 2), new ModelStopped(ModelStopReason.End));

        var transcript = await RunAsync(database, model, ["Sam", "Find Winter books.", "/audit", "/quit"], demo: true);

        Assert.All(model.Requests, request => Assert.Equal(BookshopAgent.Demo, request.ContextManagement));
        Assert.Equal(50_000, BookshopAgent.Demo.CompactAt);
        Assert.Equal(4, BookshopAgent.Demo.ClearToolResults!.After);
        InOrder(
            transcript,
            "  < search_books: ok",
            "Found it.",
            "  ~ Conversation compacted: 52,753 tokens summarized into 578.",
            "  ~ Old tool results cleared: 2 tool calls, 4,892 tokens.",
            "you> /audit",
            "Compacted",
            "52,753 tokens summarized into 578.",
            "Cleared",
            "Results of 2 tool calls cleared: 4,892 tokens.");
    }

    [DatabaseFact]
    public async Task Outside_demo_mode_compaction_and_clearing_come_later()
    {
        var model = Model().Reply("Hello.");

        await RunAsync(database, model, ["Sam", "Hi.", "/quit"]);

        var settings = Assert.Single(model.Requests).ContextManagement!;
        Assert.Equal(BookshopAgent.LongConversations, settings);
        Assert.True(settings.CompactAt > BookshopAgent.Demo.CompactAt);
        Assert.True(settings.ClearToolResults!.After > BookshopAgent.Demo.ClearToolResults!.After);
    }
}
