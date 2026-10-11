using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// Sessions, the status line and budgets, end to end. Each <see cref="ConsoleSession.RunAsync"/> is an application start:
/// what one leaves behind, the next finds only in the database.
/// </summary>
public class SessionTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private static ModelEvent[] Say(string text, Usage usage) =>
        [new TextDelta(text), new BlockReceived(ScriptedModel.TextBlock(text)), new UsageReceived(usage), new ModelStopped(ModelStopReason.End)];

    private Task<string> StoredAsync(string session) =>
        database.ScalarAsync<string>("select conversation from sessions where id = $1", session);

    [DatabaseFact]
    public async Task APP_10_quit_restart_and_resume_continues_the_session_with_its_prefix_byte_identical()
    {
        var before = Model()
            .Reply(SayThenCall("Checking.", Call("c1", "get_book", new { bookId = 144 })))
            .Reply("The Winter Archive is in stock.");
        var first = await RunAsync(database, before, ["Sam", "Is book 144 in stock?", "/quit"]);
        var id = SessionId(first);

        var after = Model().Reply("It costs £6.28.");
        var second = await RunAsync(database, after, ["Sam", "/sessions", $"/resume {id}", "What does it cost?", "/quit"]);

        InOrder(second, "Sessions, most recent first:\n", $"  {id}  ", "  Sam  (no title yet)  $", $"you> /resume {id}\n", $"Resumed session {id}: 5 messages", "It costs £6.28.");
        Assert.Empty(PrefixStability.Problems([.. before.Requests, .. after.Requests]));

        // The run context was sent when the session started, and the day and staff member have not changed since.
        Assert.Equal([Role.User, Role.Operator, Role.Assistant, Role.User, Role.Assistant, Role.User], after.Requests[0].Messages.Select(message => message.Role));
        Assert.Equal(7, System.Text.Json.JsonSerializer.Deserialize<Conversation>(await StoredAsync(id))!.Messages.Length);
    }

    [DatabaseFact]
    public async Task APP_10_a_crash_mid_reply_loses_at_most_the_step_in_flight_and_the_session_resumes()
    {
        var stock = await database.ScalarAsync<int>("select quantity from stock where book_id = 320");
        var before = Model().Reply([.. SayThenCall("I'll add two copies.", Call("c1", "restock_book", new { bookId = 320, quantity = 2 }))[..^1],
            new UsageReceived(new Usage(100, 50, 900, 200)), new ModelStopped(ModelStopReason.ToolUse)]);
        static Task Crash() => throw new InvalidOperationException("The power went out.");

        // The application stops at the approval prompt, after the model's reply was appended and before its tool ran.
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(database, before, ["Sam", "Restock book 320 with 2.", (Func<Task>)Crash]));
        var id = await database.ScalarAsync<string>("select id from sessions order by updated desc limit 1");
        var saved = System.Text.Json.JsonSerializer.Deserialize<Conversation>(await StoredAsync(id))!;
        Assert.Equal([Role.User, Role.Operator, Role.Assistant], saved.Messages.Select(message => message.Role));
        Assert.Equal("restock_book", saved.Messages[^1].Blocks[^1].ToolCall!.Name);

        var after = Model().Reply("The restock did not finish; shall I try again?");
        var transcript = await RunAsync(database, after, ["Sam", $"/resume {id}", "Did it work?", "/cost", "/quit"]);

        // The crashed reply's model call ($0.00258) is in the session's totals, though the reply never ended.
        InOrder(
            transcript,
            $"Resumed session {id}: 3 messages, $0.0026 so far.",
            "The restock did not finish",
            $"Session {id}: tokens: 1,200 in (75% from cache), 50 out; cost $0.0026 of its $5.00 budget.");
        var results = after.Requests[0].Messages[3].Blocks.Select(block => block.ToolResult!).ToList();
        Assert.True(Assert.Single(results).IsError);
        Assert.Contains("interrupted", results[0].Content, StringComparison.Ordinal);
        Assert.Null(RoleSequence.Problem(after.Requests[0].Messages));
        Assert.Equal(stock, await database.ScalarAsync<int>("select quantity from stock where book_id = 320"));
    }

    [DatabaseFact]
    public async Task A_new_session_whose_id_is_taken_fails_to_save_instead_of_overwriting_the_other()
    {
        var store = new SessionStore(database.DataSource);
        var conversation = new Conversation { Id = "taken-id-001" };
        await store.SaveAsync(conversation, "Sam", default, 0.5m, previous: null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(conversation, "Kim", default, 0, previous: null, TestContext.Current.CancellationToken));

        var kept = await store.LoadAsync(conversation.Id, TestContext.Current.CancellationToken);
        Assert.Equal(("Sam", 0.5m), (kept!.StaffMember, kept.Cost));
    }

    [DatabaseFact]
    public async Task APP_10_a_session_whose_agent_changed_is_refused_and_a_new_one_is_offered()
    {
        var first = await RunAsync(database, Model().Reply("Hello."), ["Sam", "Hi.", "/quit"]);
        var id = SessionId(first);

        var changed = Model("scripted, effort high").Reply("Hello, new session.");
        var transcript = await RunAsync(database, changed, ["Sam", $"/resume {id}", "Hi again.", "/quit"]);

        InOrder(transcript, $"Session {id} was started with another version of the assistant, so it cannot go on. Type /new to start a new session.", "Hello, new session.");
        Assert.Equal([Role.User, Role.Operator], changed.Requests[0].Messages.Select(message => message.Role));
    }

    [DatabaseFact]
    public async Task APP_02_new_starts_a_session_of_its_own_and_resume_of_an_unknown_id_says_so()
    {
        var model = Model().Reply("One.").Reply("Two.");

        var transcript = await RunAsync(database, model, ["Sam", "First.", "/new", "/resume nosuchid", "Second.", "/quit"]);

        var (first, second) = (SessionId(transcript[..transcript.IndexOf("you> /new", StringComparison.Ordinal)]), SessionId(transcript));
        Assert.NotEqual(first, second);
        InOrder(transcript, $"New session {second}.", "There is no session nosuchid. Type /sessions to list them.", "Two.");
        Assert.Equal([Role.User, Role.Operator], model.Requests[1].Messages.Select(message => message.Role));
        Assert.Contains("First.", await StoredAsync(first), StringComparison.Ordinal);
        Assert.DoesNotContain("First.", await StoredAsync(second), StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task APP_02_help_lists_every_command_and_calls_no_model()
    {
        var model = Model();

        var transcript = await RunAsync(database, model, ["Sam", "/help", "/quit"]);

        InOrder(
            transcript,
            "you> /help", "/help ", "/new ", "/sessions ", "/resume <id> ", "/cost ", "/audit [<id>] ", "/memory ", "/quit ",
            "Ctrl+C stops a reply in progress.");
        Assert.Empty(model.Requests);
    }

    [DatabaseFact]
    public async Task APP_14_the_status_line_and_cost_show_the_tokens_cache_share_and_cost_of_the_reply_and_the_session()
    {
        // At Opus 5.5's price: 100 × $4 + 50 × $20 + 900 × $0.20 + 200 × $5 per million = $0.00258; then $0.00046.
        var model = Model().Reply(Say("Hello.", new Usage(100, 50, 900, 200))).Reply(Say("Again.", new Usage(10, 10, 1_100, 0)));

        var transcript = await RunAsync(database, model, ["Sam", "Hi.", "Hi again.", "/cost", "/quit"]);

        InOrder(
            transcript,
            "Hello.\n[tokens: 1,200 in (75% from cache), 50 out · reply $0.0026 · session $0.0026]\n",
            "Again.\n[tokens: 1,110 in (99% from cache), 10 out · reply $0.0005 · session $0.0030]\n",
            "you> /cost\n",
            $"Session {SessionId(transcript)}: tokens: 2,310 in (87% from cache), 60 out; cost $0.0030 of its $5.00 budget.\n");
        Assert.Equal(0.00304m, await database.ScalarAsync<decimal>("select cost from sessions where id = $1", SessionId(transcript)));
    }

    [DatabaseFact]
    public async Task APP_14_a_reply_that_reaches_its_budget_stops_and_says_why_and_its_output_limit_is_lowered_to_what_is_left()
    {
        var model = Model()
            .Reply([.. SayThenCall("Checking.", Call("c1", "get_book", new { bookId = 144 }))[..^1], new UsageReceived(new Usage(100, 50, 0, 0)), new ModelStopped(ModelStopReason.ToolUse)])
            .Reply("Unused.");

        var transcript = await RunAsync(database, model, ["Sam", "Is book 144 in stock?", "/quit"], budgets: new Budgets(Reply: 0.001m, Session: 1m));

        InOrder(transcript, "  < get_book: ok\n", "[Stopped: this reply has reached its budget of $0.001.]\n", "[tokens: 100 in (0% from cache), 50 out · reply $0.0014 · session $0.0014]");
        Assert.Equal(50, Assert.Single(model.Requests).MaxOutputTokens);
    }

    [DatabaseFact]
    public async Task APP_14_a_session_that_reaches_its_budget_stops_the_next_reply_before_any_model_call()
    {
        var model = Model().Reply(Say("Hello.", new Usage(100, 50, 900, 200))).Reply("Unused.");

        var transcript = await RunAsync(database, model, ["Sam", "Hi.", "Hi again.", "/quit"], budgets: new Budgets(Reply: 1m, Session: 0.002m));

        InOrder(transcript, "Hello.", "you> Hi again.\n", "[Stopped: this session has reached its budget of $0.002. Type /new to start a new session.]\n");
        Assert.Equal(100, Assert.Single(model.Requests).MaxOutputTokens);
    }
}
