using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>A summary that fails is told, and the session keeps no title.</summary>
public class SummaryFailureTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    [DatabaseFact]
    public async Task APP_15_output_that_does_not_match_the_schema_is_told_and_no_summary_is_stored()
    {
        var summaries = Model().Reply("""{"title":"Greeting","summary":"Sam said hello."}""");

        var transcript = await RunAsync(database, Model().Reply("Hello."), ["Sam", "Hi.", "/quit"], summaries: summaries);
        var id = SessionId(transcript);

        InOrder(transcript, "you> /quit\n", $"[Session {id} could not be summarized: The output does not match its schema: /changes: is required]");
        Assert.Equal(DBNull.Value, await database.ScalarAsync<object>("select title from sessions where id = $1", id));

        // /sessions retries it once: a summary that fails again is not tried on every listing.
        var retried = Model().Reply("not a summary");
        var listing = await RunAsync(database, Model(), ["Sam", "/sessions", "/sessions", "/quit"], summaries: retried);

        InOrder(listing, "Summarizing 1 session left without a summary…", $"[Session {id} could not be summarized: ", $"  {id}  ", "(no title yet)", "you> /sessions\n", $"  {id}  ", "(no title yet)");
        Assert.Single(retried.Requests);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(listing, "Summarizing"));
    }
}
