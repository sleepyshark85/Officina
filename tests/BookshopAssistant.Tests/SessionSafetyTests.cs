using System.Text.Json;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
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

/// <summary>A session another console changed is never overwritten.</summary>
public class SessionConflictTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task A_save_from_a_stale_copy_fails_and_leaves_the_other_console_s_session_as_it_saved_it()
    {
        var store = new SessionStore(database.DataSource);
        var first = await store.SaveAsync(Read("shared-0001", "Hi."), "Sam", default, 0, previous: null, Ct);
        var theirs = await store.SaveAsync(Read("shared-0001", "Hi.", "Hello from the other counter."), "Sam", default, 0, previous: first, Ct);

        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Read("shared-0001", "Hi.", "Hello from here."), "Sam", default, 0, previous: first, Ct));

        Assert.Contains("changed elsewhere", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, await database.ScalarAsync<string>("select conversation from sessions where id = 'shared-0001'"));
    }

    [DatabaseFact]
    public async Task A_save_whose_earlier_save_landed_unseen_still_goes_through_as_it_loses_no_message()
    {
        var store = new SessionStore(database.DataSource);
        var first = await store.SaveAsync(Read("landed-0001", "Hi."), "Sam", default, 0, previous: null, Ct);

        // This save lands, but its answer is lost: the console still holds the first text.
        await store.SaveAsync(Read("landed-0001", "Hi.", "Hello."), "Sam", default, 0, previous: first, Ct);
        var later = await store.SaveAsync(Read("landed-0001", "Hi.", "Hello.", "Thanks."), "Sam", default, 0, previous: first, Ct);

        Assert.Equal(later, await database.ScalarAsync<string>("select conversation from sessions where id = 'landed-0001'"));
    }

    [DatabaseFact]
    public async Task A_console_whose_session_changed_elsewhere_says_so_and_does_not_overwrite_it()
    {
        // Another console saves the session between this console's replies, with a reply this console never saw.
        Task ChangeElsewhere() => database.ScalarAsync<int>(
            "update sessions set conversation = replace(conversation, 'Hello.', 'Hello from the other counter.') where staff_member = 'Sam' returning 1");
        var model = Model().Reply("Hello.").Reply("Hello again.");

        var transcript = await RunAsync(database, model, ["Sam", "Hi.", (Func<Task>)ChangeElsewhere, "Again.", "/quit"]);
        var id = SessionId(transcript);

        InOrder(transcript, "Hello again.", $"[The session could not be saved: Session {id} changed elsewhere since it was last saved here, so it was not overwritten. Type /resume {id} to go on");
        Assert.DoesNotContain("Again.", await database.ScalarAsync<string>("select conversation from sessions where id = $1", id), StringComparison.Ordinal);
    }

    private static Conversation Read(string id, params string[] texts) => JsonSerializer.Deserialize<Conversation>(
        $$"""{"id":"{{id}}","messages":[{{string.Join(",", texts.Select((text, index) => $$"""{"role":"{{(index % 2 == 0 ? "user" : "assistant")}}","blocks":[{"text":{{JsonSerializer.Serialize(text)}}}]}"""))}}]}""")!;
}
