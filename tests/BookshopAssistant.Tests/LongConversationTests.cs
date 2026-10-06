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
        Assert.Equal(12, BookshopAgent.Demo.ClearToolResults!.After);
        Assert.Equal(8, BookshopAgent.Demo.ClearToolResults!.Keep);
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
    public async Task A_compacting_reply_without_text_says_so()
    {
        var model = Model()
            .Reply(
                new BlockReceived(new ContentBlock(null, """{"type":"compaction","content":"Sam searched the catalogue."}""")),
                new BlockReceived(new ContentBlock(null, """{"type":"thinking","thinking":"","signature":"c2ln"}""")),
                new CompactionReported(85_836, 2_756), new ModelStopped(ModelStopReason.End))
            .Reply(new BlockReceived(new ContentBlock(null, """{"type":"thinking","thinking":"","signature":"c2ln"}""")), new ModelStopped(ModelStopReason.End));

        var transcript = await RunAsync(database, model, ["Sam", "How many books?", "And now?", "/quit"], demo: true);

        InOrder(
            transcript,
            "  ~ Conversation compacted: 85,836 tokens summarized into 2,756.",
            "[The conversation was compacted and the reply has no text. Please ask again.]",
            "you> And now?",
            "[The reply has no text. Please ask again.]");
    }

    [DatabaseFact]
    public async Task A_clearing_the_provider_repeats_is_shown_once_and_a_new_one_again()
    {
        var model = Model()
            .Reply([.. SayThenCall("One.", Call("c1", "get_book", new { bookId = 1 }))[..^1], new ClearingReported(42_452, 3), new ModelStopped(ModelStopReason.ToolUse)])
            .Reply([.. SayThenCall("Two.", Call("c2", "get_book", new { bookId = 2 }))[..^1], new ClearingReported(42_359, 3), new ModelStopped(ModelStopReason.ToolUse)])
            .Reply(new TextDelta("Done."), new BlockReceived(ScriptedModel.TextBlock("Done.")), new ClearingReported(56_912, 6), new ModelStopped(ModelStopReason.End));

        var transcript = await RunAsync(database, model, ["Sam", "Look up books 1 and 2.", "/audit", "/quit"], demo: true);

        InOrder(transcript, "  ~ Old tool results cleared: 3 tool calls, 42,452 tokens.", "Two.", "  ~ Old tool results cleared: 6 tool calls, 56,912 tokens.");
        Assert.DoesNotContain("42,359 tokens.", transcript.Split("you> /audit")[0], StringComparison.Ordinal);

        // The audit trail keeps every report, as the provider made it.
        Assert.Equal(3, transcript.Split("you> /audit")[1].Split("Results of ").Length - 1);
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
