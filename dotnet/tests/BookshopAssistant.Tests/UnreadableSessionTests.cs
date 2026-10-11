using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>Stored sessions the console cannot read are reported and skipped, never ending the console.</summary>
public class UnreadableSessionTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    [DatabaseFact]
    public async Task A_session_that_cannot_be_read_is_reported_by_resume_and_sessions_and_the_console_goes_on()
    {
        await Store(database, "broken-0001", "not json");
        await Store(database, "broken-0002", """{"id":"broken-0002","messages":[{"role":"user","blocks":[]}]}""");
        await Store(database, "broken-0003", """{"id":"broken-0003","messages":[null]}""");

        var transcript = await RunAsync(
            database, Model(), ["Sam", "/resume broken-0001", "/resume broken-0002", "/resume broken-0003", "/sessions", "/sessions", "/quit"], summaries: Model());

        InOrder(
            transcript,
            "The session could not be read: Session broken-0001 cannot be read: ",
            "The session could not be read: Session broken-0002 cannot be read: ",
            "The session could not be read: Session broken-0003 cannot be read: A message is null.",
            "you> /sessions\n",
            "  broken-0001  ",
            "you> /sessions\n",
            "  broken-0001  ",
            "you> /quit");
        var listings = transcript[transcript.IndexOf("you> /sessions", StringComparison.Ordinal)..];
        Assert.Contains("[Session broken-0001 cannot be read: ", listings, StringComparison.Ordinal);
        Assert.Contains("[Session broken-0002 cannot be read: ", listings, StringComparison.Ordinal);
        Assert.Contains("[Session broken-0003 cannot be read: ", listings, StringComparison.Ordinal);

        // Each is reported once by /resume and once by the first listing, which does not try them again.
        Assert.Equal(6, transcript.Split("cannot be read").Length - 1);
    }

    internal static Task<int> Store(BookshopDatabase database, string id, string conversation) => database.ScalarAsync<int>(
        "insert into sessions (id, staff_member, conversation, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, cost, updated) values ($1, 'Sam', $2, 0, 0, 0, 0, 0, now()) returning 1",
        id, conversation);
}
