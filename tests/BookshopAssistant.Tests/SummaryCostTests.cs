using System.Text.Json;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>Summaries count toward a session's totals, and one listing summarizes only a few.</summary>
public class SummaryCostTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    [DatabaseFact]
    public async Task Sessions_summarizes_at_most_three_stale_sessions_a_listing_and_adds_each_summary_s_cost_to_its_session()
    {
        for (var index = 1; index <= 4; index++)
        {
            await UnreadableSessionTests.Store(database, $"stale-000{index}", Conversation($"stale-000{index}"));
        }

        var summaries = Model();
        for (var index = 0; index < 4; index++)
        {
            summaries.Reply(
                new BlockReceived(ScriptedModel.TextBlock(JsonSerializer.Serialize(new { title = "Greeting", summary = "Sam said hello.", changes = Array.Empty<string>() }))),
                new UsageReceived(new Usage(1_000, 100, 0, 0)),
                new ModelStopped(ModelStopReason.End));
        }

        var transcript = await RunAsync(database, Model(), ["Sam", "/sessions", "/sessions", "/quit"], summaries: summaries);

        InOrder(
            transcript,
            "Summarizing 3 of 4 sessions left without a summary; /sessions again does more…",
            "you> /sessions\n",
            "Summarizing 1 session left without a summary…");
        Assert.Equal(4, summaries.Requests.Count);

        // At Opus 5.5's price, 1,000 input and 100 output tokens cost $0.006 a summary.
        Assert.Equal(0.024m, await database.ScalarAsync<decimal>("select sum(cost) from sessions where id like 'stale-%'"));
        Assert.Equal(4_000L, await database.ScalarAsync<long>("select sum(input_tokens)::bigint from sessions where id like 'stale-%'"));
    }

    private static string Conversation(string id) =>
        $$"""{"id":"{{id}}","messages":[{"role":"user","blocks":[{"text":"Hi."}]},{"role":"assistant","blocks":[{"text":"Hello."}]}]}""";
}
