using System.Text.Json;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// TEST-09: the session summarizer (APP-15) end to end. Every session these tests leave behind ends up summarized, so
/// the tests of this class see no stale session of another's in <c>/sessions</c>.
/// </summary>
public class SummaryTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private static string Summary(string title, string summary, params string[] changes) =>
        JsonSerializer.Serialize(new { title, summary, changes });

    [DatabaseFact]
    public async Task APP_15_leaving_a_session_summarizes_it_and_sessions_shows_its_title_summary_and_changes()
    {
        var model = Model()
            .Reply(SayThenCall("I'll add two copies.", Call("c1", "restock_book", new { bookId = 320, quantity = 2 })))
            .Reply("Done: two more copies.");
        var summaries = Model().Reply(Summary("Restock of book 320", "Sam asked for two more copies of book 320.", "Book 320: 2 copies added"));

        var first = await RunAsync(database, model, ["Sam", "Restock book 320 with 2.", "y", "/quit"], summaries: summaries);
        var id = SessionId(first);
        var listing = await RunAsync(database, Model(), ["Sam", "/sessions", "/quit"], summaries: Model());

        InOrder(first, "Done: two more copies.", "you> /quit\n", $"Session {id} summarized: Restock of book 320\n");
        InOrder(listing, $"  {id}  ", "  Sam  Restock of book 320  $", "\n    Sam asked for two more copies of book 320.\n", "    Changes: Book 320: 2 copies added\n");

        // The summarizer reads the transcript as plain text in one user message, with no tools and the output schema.
        var request = Assert.Single(summaries.Requests);
        Assert.Equal((0, Role.User), (request.Prefix.Tools.Length, Assert.Single(request.Messages).Role));
        Assert.NotNull(request.Prefix.OutputSchema);
        InOrder(
            request.Messages[0].Text,
            "Staff: Restock book 320 with 2.\n",
            "Assistant: I'll add two copies.\n",
            """Tool call restock_book {"bookId":320,"quantity":2}""",
            "Tool result of restock_book: ",
            "Assistant: Done: two more copies.\n");
        Assert.DoesNotContain("Today is", request.Messages[0].Text, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task APP_15_new_resume_and_quit_each_summarize_the_session_left_and_an_unchanged_session_is_not_summarized_again()
    {
        var summaries = Model()
            .Reply(Summary("Greeting", "Sam said hello."))
            .Reply(Summary("Second greeting", "Sam said hello again."))
            .Reply(Summary("Third greeting", "Sam said hello a third time."));

        var first = await RunAsync(database, Model().Reply("Hello.").Reply("Hello again."), ["Sam", "Hi.", "/new", "Hi again.", "/quit"], summaries: summaries);
        var (one, two) = (SessionId(first[..first.IndexOf("you> /new", StringComparison.Ordinal)]), SessionId(first));
        var second = await RunAsync(database, Model().Reply("Hello a third time."), ["Sam", "Hi a third time.", $"/resume {one}", "/quit"], summaries: summaries);
        var three = SessionId(second[..second.IndexOf("you> /resume", StringComparison.Ordinal)]);

        InOrder(first, "you> /new\n", $"Session {one} summarized: Greeting\n", $"New session {two}.", "you> /quit\n", $"Session {two} summarized: Second greeting\n");
        InOrder(second, $"Resumed session {one}", $"Session {three} summarized: Third greeting\n", "you> /quit\n");
        Assert.DoesNotContain($"Session {one} summarized", second, StringComparison.Ordinal);
        Assert.Equal(3, summaries.Requests.Count);
        Assert.Equal("Greeting", await database.ScalarAsync<string>("select title from sessions where id = $1", one));
    }

    [DatabaseFact]
    public async Task APP_15_resuming_the_session_in_use_does_not_leave_it_and_the_end_of_input_leaves_it()
    {
        var summaries = Model().Reply(Summary("Greeting", "Sam said hello.")).Reply(Summary("Two greetings", "Sam said hello twice."));
        var first = await RunAsync(database, Model().Reply("Hello."), ["Sam", "Hi."], summaries: summaries);
        var id = SessionId(first);

        var second = await RunAsync(database, Model().Reply("Hello again."), ["Sam", $"/resume {id}", "Hi again.", $"/resume {id}", "/quit"], summaries: summaries);

        Assert.EndsWith($"Session {id} summarized: Greeting\n", first, StringComparison.Ordinal);
        InOrder(second, "Hello again.", $"you> /resume {id}\n", $"Resumed session {id}", "you> /quit\n", $"Session {id} summarized: Two greetings\n");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "summarized:"));
        Assert.Contains("Staff: Hi again.", summaries.Requests[1].Messages[0].Text, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task APP_15_a_session_left_without_a_summary_after_a_crash_is_summarized_when_sessions_lists_it()
    {
        var model = Model().Reply("Book 144 is in stock.");
        static Task Crash() => throw new InvalidOperationException("The power went out.");
        var unused = Model();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(database, model, ["Sam", "Is book 144 in stock?", (Func<Task>)Crash], summaries: unused));
        var id = await database.ScalarAsync<string>("select id from sessions order by updated desc limit 1");
        Assert.Empty(unused.Requests);

        var summaries = Model().Reply(Summary("Stock of book 144", "Sam checked that book 144 is in stock."));
        var transcript = await RunAsync(database, Model(), ["Sam", "/sessions", "/sessions", "/quit"], summaries: summaries);

        InOrder(transcript, "you> /sessions\n", $"  {id}  ", "  Sam  Stock of book 144  $", "    Sam checked that book 144 is in stock.\n", "you> /sessions\n", "  Sam  Stock of book 144  $");
        Assert.Single(summaries.Requests);
        Assert.Contains("Staff: Is book 144 in stock?", summaries.Requests[0].Messages[0].Text, StringComparison.Ordinal);
    }
}

/// <summary>TEST-09: a summary that fails (APP-15, OUT-02) is told, and the session keeps no title.</summary>
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

        // /sessions tries it again, once: a summary that fails again is not tried on every listing.
        var retried = Model().Reply("not a summary");
        var listing = await RunAsync(database, Model(), ["Sam", "/sessions", "/sessions", "/quit"], summaries: retried);

        InOrder(listing, "Summarizing 1 session left without a summary…", $"[Session {id} could not be summarized: ", $"  {id}  ", "(no title yet)", "you> /sessions\n", $"  {id}  ", "(no title yet)");
        Assert.Single(retried.Requests);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(listing, "Summarizing"));
    }
}
