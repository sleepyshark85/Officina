using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// Each console flow, end to end: the real console, agent, core and tools against the real database; only the model
/// and the staff member are scripted.
/// </summary>
public class EndToEndTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private const string Alice = """{"nameOrEmail":"Alice Martin"}""";

    private Task<int> StockAsync(int bookId) => database.ScalarAsync<int>("select quantity from stock where book_id = $1", bookId);

    [DatabaseFact]
    public async Task APP_01_the_reply_streams_with_the_text_between_tool_calls_and_each_tool_with_its_input_and_outcome()
    {
        var model = Model()
            .Reply(SayThenCall("Let me look that up.", Call("c1", "search_books", new { title = "Winter Archive" })))
            .Reply(new TextDelta("We have "), new TextDelta("12 copies."), new BlockReceived(ScriptedModel.TextBlock("We have 12 copies.")), new ModelStopped(ModelStopReason.End));

        var transcript = await RunAsync(database, model, ["Sam", "Do we have The Winter Archive?", "/quit"]);

        InOrder(
            transcript,
            "Who is using the assistant? Your name: Sam",
            "you> Do we have The Winter Archive?",
            "assistant> Let me look that up.\n",
            """  > search_books {"title":"Winter Archive"}""",
            "  < search_books: ok\n",
            "We have 12 copies.\n",
            "you> /quit");
        Assert.Contains("\"title\":\"The Winter Archive\"", LastResults(model)[0].Content, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task APP_03_cancelling_stops_the_reply_and_the_session_goes_on()
    {
        var model = Model()
            .Reply(new TextDelta("Let me think about every book "), new TextDelta("we have ever sold..."), new BlockReceived(ScriptedModel.TextBlock("…")), new ModelStopped(ModelStopReason.End))
            .Reply("Hello again.");

        var transcript = await RunAsync(database, model, ["Sam", "Tell me everything.", "Hello?", "/quit"], cancelOn: "every book ");

        InOrder(transcript, "assistant> Let me think about every book \n[Cancelled.]", "you> Hello?", "assistant> Hello again.");
        Assert.DoesNotContain("we have ever sold", transcript, StringComparison.Ordinal);

        // The cancelled exchange left nothing behind: the second request holds only the new message and the run context.
        Assert.Equal([Role.User, Role.Operator], model.Requests[1].Messages.Select(message => message.Role));
        Assert.Equal("Hello?", model.Requests[1].Messages[0].Text);
    }

    [DatabaseFact]
    public async Task APP_03_cancelling_at_the_approval_prompt_stops_the_reply_at_once_and_the_change_is_not_made()
    {
        var stock = await StockAsync(320);
        var model = Model()
            .Reply(SayThenCall("I'll add two copies.", Call("c1", "restock_book", new { bookId = 320, quantity = 2 })))
            .Reply("Hello again.");

        var transcript = await RunAsync(database, model, ["Sam", "Restock book 320 with 2.", "Hello?", "/quit"], cancelOn: "Approve? [y/N] ");

        // The line typed after the cancel is the next message, not an answer to the cancelled prompt.
        InOrder(transcript, "    Approve? [y/N] ", "  < restock_book: error: The call was cancelled while waiting for approval.", "[Cancelled.]", "you> Hello?", "Hello again.");
        Assert.Equal(stock, await StockAsync(320));
    }

    [DatabaseFact]
    public async Task APP_05_the_read_tools_of_one_reply_all_answer_from_the_database()
    {
        var model = Model()
            .Reply(SayThenCall(
                "Looking.",
                Call("c1", "find_customer", new { nameOrEmail = "Alice Martin" }),
                Call("c2", "list_customer_orders", new { customerId = 1 }),
                Call("c3", "get_book", new { bookId = 216 }),
                Call("c4", "search_books", new { genre = "Fantasy", inStock = true, limit = 2 })))
            .Reply(SayThenCall("And order 77.", Call("c5", "get_order", new { orderId = 77 })))
            .Reply("Here is what I found.");

        var transcript = await RunAsync(database, model, ["Sam", "What do we know about Alice Martin?", "/quit"]);

        var reads = model.Requests[1].Messages[^1].Blocks.Select(block => block.ToolResult!).ToList();
        Assert.All(reads, result => Assert.False(result.IsError, result.Content));
        Assert.Contains("\"name\":\"Alice Martin\"", reads[0].Content, StringComparison.Ordinal);
        Assert.Contains("\"status\":", reads[1].Content, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"The Hollow Island\"", reads[2].Content, StringComparison.Ordinal);
        Assert.Contains("\"id\":144", reads[3].Content, StringComparison.Ordinal);
        Assert.False(LastResults(model)[0].IsError, LastResults(model)[0].Content);
        InOrder(transcript, "  < find_customer: ok", "  < get_order: ok", "Here is what I found.");
    }

    [DatabaseFact]
    public async Task APP_06_a_write_shows_its_exact_input_for_approval_and_runs_only_if_approved()
    {
        var (stock300, stock301) = (await StockAsync(300), await StockAsync(301));
        var model = Model()
            .Reply(SayThenCall("I'll add five copies.", Call("c1", "restock_book", new { bookId = 300, quantity = 5 })))
            .Reply("Done: five more copies.")
            .Reply(SayThenCall("I'll add three copies.", Call("c2", "restock_book", new { bookId = 301, quantity = 3 })))
            .Reply("Understood, I left the stock as it was.");

        var transcript = await RunAsync(database, model, ["Sam", "Restock book 300 with 5.", "y", "Restock book 301 with 3.", "n", "/quit"]);

        InOrder(
            transcript,
            "  ? restock_book needs your approval. Its exact input:\n",
            """    {"bookId":300,"quantity":5}""",
            "    Approve? [y/N] y",
            "  < restock_book: ok",
            "Done: five more copies.",
            """    {"bookId":301,"quantity":3}""",
            "    Approve? [y/N] n",
            "  < restock_book: error: The call was denied: the staff member declined",
            "Understood, I left the stock as it was.");
        Assert.Equal((stock300 + 5, stock301), (await StockAsync(300), await StockAsync(301)));
        Assert.True(LastResults(model)[0].IsError);
    }

    [DatabaseFact]
    public async Task APP_07_not_enough_stock_comes_back_as_an_error_result_and_the_model_recovers_in_the_same_reply()
    {
        var stock = await StockAsync(310);
        var tooMany = Call("c1", "place_order", new { customerId = 1, lines = new[] { new { bookId = 310, quantity = stock + 10 } } });
        var enough = Call("c2", "place_order", new { customerId = 1, lines = new[] { new { bookId = 310, quantity = stock } } });
        var model = Model()
            .Reply(SayThenCall("Placing the order.", tooMany))
            .Reply(SayThenCall($"Only {stock} are in stock; I'll order those instead.", enough))
            .Reply($"Ordered all {stock} copies.");

        var transcript = await RunAsync(database, model, ["Sam", $"Order {stock + 10} copies of book 310 for Alice.", "y", "y", "/quit"]);

        var refused = model.Requests[1].Messages[^1].Blocks.Single().ToolResult!;
        Assert.True(refused.IsError);
        Assert.StartsWith("Not enough stock for", refused.Content, StringComparison.Ordinal);
        InOrder(transcript, "  < place_order: error: Not enough stock", $"Only {stock} are in stock", "  < place_order: ok", $"Ordered all {stock} copies.");
        Assert.Equal(0, await StockAsync(310));
    }

    [DatabaseFact]
    public async Task APP_09_a_multi_step_request_finds_searches_orders_after_approval_and_answers()
    {
        var (stock144, stock216) = (await StockAsync(144), await StockAsync(216));
        var orders = await database.ScalarAsync<long>("select count(*) from orders");
        var model = Model()
            .Reply(SayThenCall(
                "Let me find Alice and the cheapest fantasy books in stock.",
                Call("c1", "find_customer", new { nameOrEmail = "Alice Martin" }),
                Call("c2", "search_books", new { genre = "Fantasy", inStock = true, limit = 2 })))
            .Reply(SayThenCall(
                "I'll order The Winter Archive and The Hollow Island for Alice Martin.",
                Call("c3", "place_order", new { customerId = 1, lines = new[] { new { bookId = 144, quantity = 1 }, new { bookId = 216, quantity = 1 } } })))
            .Reply("Order placed for Alice Martin: The Winter Archive (£6.28) and The Hollow Island (£6.92). Total £13.20.");

        var transcript = await RunAsync(
            database, model, ["Sam", "Order the two cheapest fantasy books in stock for Alice Martin and tell me the total", "y", "/quit"]);

        // The two reads run in parallel, so only each one's own lines are ordered, all before the write.
        InOrder(transcript, $"  > find_customer {Alice}", "  < find_customer: ok", "  > place_order");
        InOrder(transcript, "  > search_books", "  < search_books: ok", "  > place_order");
        InOrder(
            transcript,
            "  > place_order",
            """    {"customerId":1,"lines":[{"bookId":144,"quantity":1},{"bookId":216,"quantity":1}]}""",
            "    Approve? [y/N] y",
            "  < place_order: ok",
            "Total £13.20.");
        Assert.Contains("\"total\":13.20", LastResults(model)[0].Content, StringComparison.Ordinal);
        Assert.Equal((stock144 - 1, stock216 - 1, orders + 1), (await StockAsync(144), await StockAsync(216), await database.ScalarAsync<long>("select count(*) from orders")));
        Assert.Equal(13.20m, await database.ScalarAsync<decimal>("select total from orders order by id desc limit 1"));
    }

    [DatabaseFact]
    public async Task APP_13_the_run_context_names_the_date_and_staff_member_and_is_sent_again_only_on_a_new_day()
    {
        // Messages come at 08:00 and 20:00 on Monday, then 08:00 on Tuesday.
        var time = new FakeTimeProvider(Start);
        var later = (Func<Task>)(() =>
        {
            time.Advance(TimeSpan.FromHours(12));
            return Task.CompletedTask;
        });
        var model = Model().Reply("Good morning.").Reply("Good evening.").Reply("Good morning again.");

        await RunAsync(database, model, ["Sam", "Morning!", later, "Evening!", later, "Next morning!", "/quit"], time: time);

        var contexts = model.Requests[^1].Messages.Where(message => message.Role == Role.Operator).Select(message => message.Text);
        Assert.Equal(
            [
                "Today is Monday 5 October 2026. The staff member using the assistant is Sam.",
                "Today is Tuesday 6 October 2026. The staff member using the assistant is Sam.",
            ],
            contexts);
        Assert.Equal([Role.User, Role.Operator, Role.Assistant, Role.User, Role.Assistant, Role.User, Role.Operator], model.Requests[^1].Messages.Select(message => message.Role));
        Assert.DoesNotContain("Sam", model.Requests[0].Prefix.Instructions, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task APP_18_with_the_database_down_tools_return_errors_and_once_it_is_back_the_session_works_again()
    {
        var model = Model()
            .Reply(SayThenCall("Checking.", Call("c1", "get_book", new { bookId = 144 })))
            .Reply("I can't reach the database right now; please try again shortly.")
            .Reply(SayThenCall("Checking again.", Call("c2", "get_book", new { bookId = 144 })))
            .Reply("The Winter Archive is in stock.");
        await database.TakeDownAsync();
        var transcript = await RunAsync(
            database, model, ["Sam", "Is book 144 in stock?", (Func<Task>)database.BringBackAsync, "And now?", "/quit"]);

        InOrder(transcript, "  < get_book: error: ", "I can't reach the database", "  < get_book: ok", "The Winter Archive is in stock.");
        Assert.True(model.Requests[1].Messages[^1].Blocks.Single().ToolResult!.IsError);
        Assert.False(LastResults(model)[0].IsError);
    }
}
