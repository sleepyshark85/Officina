using System.Globalization;
using System.Runtime.CompilerServices;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// The live smoke tests: the real console, Claude in demo mode and the database in Docker, with only the staff member
/// scripted. One places an order with approval; the other reaches a compaction with the demo script's catalogue
/// searches (<c>docs/demo.md</c>). Both check that every model call after the first reads the cache. Together they cost
/// about $0.40, so they run only on demand: see the README.
/// </summary>
[Trait("Category", "Live")]
public class LiveSmokeTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    /// <summary>The order request, as in the demo script.</summary>
    private const string Order = "Order the two cheapest fantasy books in stock for Alice Martin and tell me the total.";

    /// <summary>
    /// The demo script's four 10–15k-token searches, which together cross 50,000 input tokens. The demo asks one a turn; here
    /// they come in one turn, which costs less.
    /// </summary>
    private const string Searches = """
        Run these four catalogue searches together, then just give me the four counts: every book priced at most £18 (up to 300 of them),
        the 250 cheapest books in stock or not, every book in stock priced at most £18 (up to 300), and every book priced at most £16 (up to 300).
        """;

    [LiveFact]
    public async Task APP_09_places_the_order_after_approval_and_reads_the_cache_from_the_second_call()
    {
        var (transcript, cacheReads) = await RunLiveAsync(["Smoke", Order, "y", "/quit"], new(Reply: 0.20m, Session: 0.20m));

        // The order holds the two cheapest fantasy books in stock, one copy each, and the reply gives its total.
        var expected = await database.ScalarAsync<int[]>("""
            select array_agg(id order by id) from (
                select b.id from books b join genres g on g.id = b.genre_id join stock s on s.book_id = b.id
                where g.name = 'Fantasy' and s.quantity > 0 order by b.price, b.title limit 2) cheapest
            """);
        var newest = await database.ScalarAsync<int>("select max(id) from orders");
        Assert.True(newest > 80, $"No order was placed.\n{transcript}");
        Assert.Equal(1, await database.ScalarAsync<int>("select customer_id from orders where id = $1", newest));
        Assert.Equal(expected, await database.ScalarAsync<int[]>("select array_agg(book_id order by book_id) from order_lines where order_id = $1 and quantity = 1", newest));
        var total = await database.ScalarAsync<decimal>("select total from orders where id = $1", newest);
        InOrder(transcript, "  ? place_order needs your approval.", "  < place_order: ok", total.ToString("0.00", CultureInfo.InvariantCulture));
        AssertCacheReadFromTheSecondCall(cacheReads);
    }

    [LiveFact]
    public async Task Demo_mode_compacts_after_the_demo_scripts_searches()
    {
        var (transcript, cacheReads) = await RunLiveAsync(["Smoke", Searches.ReplaceLineEndings(" "), "/quit"], new(Reply: 0.40m, Session: 0.40m));

        Assert.Contains("  ~ Conversation compacted:", transcript, StringComparison.Ordinal);
        AssertCacheReadFromTheSecondCall(cacheReads);
    }

    /// <summary>Runs the console on Claude in demo mode; returns the transcript and each model call's cache reads, in order.</summary>
    private async Task<(string Transcript, List<double> CacheReads)> RunLiveAsync(IEnumerable<object> script, Budgets budgets)
    {
        using var telemetry = new TelemetryCollector();
        using var model = BookshopAgent.Model(demo: true);
        var transcript = await RunAsync(database, model, script, budgets: budgets, demo: true);

        var calls = telemetry.Measurements("bookshop").Where(measured => (string?)measured.Tags["gen_ai.provider.name"] == "anthropic").ToList();
        var inputs = calls.Where(measured => measured.Instrument == "gen_ai.client.token.usage" && (string?)measured.Tags["gen_ai.token.type"] == "input").Select(measured => measured.Value).ToList();
        var cacheReads = calls.Where(measured => measured.Instrument == "officina.model.cache_tokens" && (string?)measured.Tags["officina.cache.type"] == "read").Select(measured => measured.Value).ToList();
        TestContext.Current.TestOutputHelper?.WriteLine(transcript);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(
            "\n", inputs.Zip(cacheReads, (input, read) => (input, read)).Select((call, index) => string.Create(
                CultureInfo.InvariantCulture, $"call {index + 1}: {call.input:N0} input tokens, {call.read:N0} read from the cache"))));
        Assert.DoesNotContain("[Failed", transcript, StringComparison.Ordinal);
        Assert.Equal(inputs.Count, cacheReads.Count);
        return (transcript, cacheReads);
    }

    private static void AssertCacheReadFromTheSecondCall(List<double> cacheReads)
    {
        Assert.True(cacheReads.Count >= 3, $"Expected several model calls, got {cacheReads.Count}.");
        Assert.All(cacheReads.Skip(1), read => Assert.True(read > 0, $"A model call read nothing from the cache: {string.Join(", ", cacheReads)}"));
    }
}

/// <summary>
/// A live test: skipped unless <c>OFFICINA_LIVE_TESTS=1</c> and <c>ANTHROPIC_API_KEY</c> are set where the database tests
/// run (<see cref="BookshopDatabase.Available"/>), so neither <c>dotnet test</c> nor CI spends on it.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Live: set OFFICINA_LIVE_TESTS=1 and ANTHROPIC_API_KEY, on Linux with Docker.";
        SkipUnless = nameof(Enabled);
        SkipType = typeof(LiveFactAttribute);
    }

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("OFFICINA_LIVE_TESTS") == "1"
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        && BookshopDatabase.Available;
}
