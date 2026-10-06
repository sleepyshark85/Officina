using System.Text.Json;
using Sleepyshark.Officina;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

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
