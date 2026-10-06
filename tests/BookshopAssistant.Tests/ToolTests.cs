using System.Text.Json;
using Sleepyshark.Officina;

namespace BookshopAssistant.Tests;

/// <summary>The nine tools against the real seeded database.</summary>
public class ToolTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private const int Alice = 1;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private BookshopTools Tools => new(database.DataSource);

    private static JsonElement Ok(ToolOutput output)
    {
        Assert.False(output.IsError, output.Content);
        return JsonDocument.Parse(output.Content).RootElement;
    }

    private static string Error(ToolOutput output)
    {
        Assert.True(output.IsError, output.Content);
        return output.Content;
    }

    private Task<int> StockAsync(int bookId) => database.ScalarAsync<int>("select quantity from stock where book_id = $1", bookId);

    private Task<long> OrdersAsync() => database.ScalarAsync<long>("select count(*) from orders");

    [DatabaseFact]
    public async Task Search_filters_by_genre_and_stock_and_lists_the_cheapest_first()
    {
        var books = Ok(await Tools.SearchBooksAsync(genre: "fantasy", inStock: true, limit: 3, cancellationToken: Ct)).EnumerateArray().ToList();

        // Book 72 is the cheapest fantasy book, and out of stock.
        Assert.Equal([144, 216, 288], books.Select(book => book.GetProperty("id").GetInt32()));
        Assert.All(books, book => Assert.Equal("Fantasy", book.GetProperty("genre").GetString()));
        Assert.All(books, book => Assert.True(book.GetProperty("stock").GetInt32() > 0));
        Assert.Equal(72, Ok(await Tools.SearchBooksAsync(genre: "Fantasy", limit: 1, cancellationToken: Ct))[0].GetProperty("id").GetInt32());
    }

    [DatabaseFact]
    public async Task Search_matches_part_of_the_title_or_author_and_a_highest_price()
    {
        var byTitle = Ok(await Tools.SearchBooksAsync(title: "silver tide", cancellationToken: Ct));
        var byAuthor = Ok(await Tools.SearchBooksAsync(author: "OKAFOR", maxPrice: 10m, limit: 100, cancellationToken: Ct)).EnumerateArray().ToList();

        Assert.Equal("The Silver Tide", Assert.Single(byTitle.EnumerateArray().ToList()).GetProperty("title").GetString());
        Assert.NotEmpty(byAuthor);
        Assert.All(byAuthor, book => Assert.Contains("Okafor", book.GetProperty("author").GetString(), StringComparison.Ordinal));
        Assert.All(byAuthor, book => Assert.True(book.GetProperty("price").GetDecimal() <= 10m));
    }

    [DatabaseFact]
    public async Task Search_text_is_taken_literally_so_a_wildcard_or_backslash_matches_only_itself()
    {
        Assert.Empty(Ok(await Tools.SearchBooksAsync(title: "_", cancellationToken: Ct)).EnumerateArray());
        Assert.Empty(Ok(await Tools.SearchBooksAsync(author: "%", cancellationToken: Ct)).EnumerateArray());
        Assert.Empty(Ok(await Tools.FindCustomerAsync("_", Ct)).EnumerateArray());
        Assert.Empty(Ok(await Tools.FindCustomerAsync(@"alice\", Ct)).EnumerateArray());
        Assert.Single(Ok(await Tools.FindCustomerAsync("MARTIN@example", Ct)).EnumerateArray());
    }

    [DatabaseFact]
    public async Task A_broad_search_returns_10_to_15k_tokens_within_the_result_limit()
    {
        var output = await Tools.SearchBooksAsync(limit: BookshopTools.MaxSearchResults, cancellationToken: Ct);

        // About four characters a token for this JSON, under the core's result limit, so nothing is cut.
        Assert.Equal(BookshopTools.MaxSearchResults, Ok(output).GetArrayLength());
        Assert.InRange(output.Content.Length, 40_000, 60_000);
    }

    [DatabaseFact]
    public async Task Reads_find_customers_their_orders_books_and_orders()
    {
        var customer = Assert.Single(Ok(await Tools.FindCustomerAsync("alice", Ct)).EnumerateArray().ToList());
        var orders = Ok(await Tools.ListCustomerOrdersAsync(Alice, Ct)).EnumerateArray().ToList();
        var order = Ok(await Tools.GetOrderAsync(orders[0].GetProperty("id").GetInt32(), Ct));
        var book = Ok(await Tools.GetBookAsync(144, Ct));

        Assert.Equal((Alice, "Alice Martin"), (customer.GetProperty("id").GetInt32(), customer.GetProperty("name").GetString()));
        Assert.NotEmpty(orders);
        Assert.Equal("Alice Martin", order.GetProperty("order").GetProperty("customer").GetString());
        Assert.Equal(
            order.GetProperty("order").GetProperty("total").GetDecimal(),
            order.GetProperty("lines").EnumerateArray().Sum(line => line.GetProperty("quantity").GetInt32() * line.GetProperty("unitPrice").GetDecimal()));
        Assert.Equal(("The Winter Archive", "Fantasy", 6.28m), (book.GetProperty("title").GetString(), book.GetProperty("genre").GetString(), book.GetProperty("price").GetDecimal()));
    }

    [DatabaseFact]
    public async Task Unknown_ids_are_error_results()
    {
        Assert.Equal("There is no book with id 9999.", Error(await Tools.GetBookAsync(9999, Ct)));
        Assert.Equal("There is no customer with id 9999.", Error(await Tools.ListCustomerOrdersAsync(9999, Ct)));
        Assert.Equal("There is no order with id 9999.", Error(await Tools.GetOrderAsync(9999, Ct)));
        Assert.Equal("There is no order with id 9999.", Error(await Tools.CancelOrderAsync(9999, Ct)));
        Assert.Equal("There is no book with id 9999.", Error(await Tools.RestockBookAsync(9999, 1, Ct)));
        Assert.Empty(Ok(await Tools.FindCustomerAsync("nobody at all", Ct)).EnumerateArray());
    }

    [DatabaseFact]
    public async Task Add_customer_adds_one_and_refuses_a_second_with_the_same_email()
    {
        var added = Ok(await Tools.AddCustomerAsync("Zoe Park", "zoe.park@example.com", Ct));

        Assert.Equal("Zoe Park", Assert.Single(Ok(await Tools.FindCustomerAsync("zoe.park", Ct)).EnumerateArray().ToList()).GetProperty("name").GetString());
        Assert.True(added.GetProperty("id").GetInt32() > 40);
        Assert.Equal("A customer with the email zoe.park@example.com already exists.", Error(await Tools.AddCustomerAsync("Zoe P", "zoe.park@example.com", Ct)));
    }

    [DatabaseFact]
    public async Task Place_order_takes_the_copies_from_stock_and_charges_the_current_prices()
    {
        var (stock160, stock161) = (await StockAsync(160), await StockAsync(161));
        var (price160, price161) = (Ok(await Tools.GetBookAsync(160, Ct)).GetProperty("price").GetDecimal(), Ok(await Tools.GetBookAsync(161, Ct)).GetProperty("price").GetDecimal());

        var order = Ok(await Tools.PlaceOrderAsync(Alice, [new(160, 2), new(161, 1)], Ct));

        Assert.Equal(2 * price160 + price161, order.GetProperty("total").GetDecimal());
        Assert.Equal((stock160 - 2, stock161 - 1), (await StockAsync(160), await StockAsync(161)));
        var stored = Ok(await Tools.GetOrderAsync(order.GetProperty("orderId").GetInt32(), Ct));
        Assert.Equal("placed", stored.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(2 * price160 + price161, stored.GetProperty("order").GetProperty("total").GetDecimal());
    }

    [DatabaseFact]
    public async Task Business_rule_failures_are_error_results_and_change_nothing()
    {
        var (stock170, orders) = (await StockAsync(170), await OrdersAsync());

        Assert.Equal(
            $"Not enough stock for \"The Crimson Clockmaker\" (id 170): {stock170 + 1} requested, {stock170} in stock. Nothing was ordered.",
            Error(await Tools.PlaceOrderAsync(Alice, [new(171, 1), new(170, stock170 + 1)], Ct)));
        Assert.Equal("There is no customer with id 9999.", Error(await Tools.PlaceOrderAsync(9999, [new(170, 1)], Ct)));
        Assert.Equal("There is no book with id 9999. Nothing was ordered.", Error(await Tools.PlaceOrderAsync(Alice, [new(9999, 1)], Ct)));
        Assert.StartsWith("An order needs", Error(await Tools.PlaceOrderAsync(Alice, [new(170, 1), new(170, 1)], Ct)), StringComparison.Ordinal);
        Assert.StartsWith("An order needs", Error(await Tools.PlaceOrderAsync(Alice, [], Ct)), StringComparison.Ordinal);
        Assert.Equal("Restock at least one copy.", Error(await Tools.RestockBookAsync(170, 0, Ct)));
        Assert.Equal((stock170, orders), (await StockAsync(170), await OrdersAsync()));
    }

    [DatabaseFact]
    public async Task Concurrent_orders_never_take_more_copies_than_are_in_stock()
    {
        var stock = await StockAsync(180);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, stock + 3).Select(_ => Tools.PlaceOrderAsync(Alice, [new(180, 1)], Ct)));

        Assert.Equal(stock, outcomes.Count(output => !output.IsError));
        Assert.All(outcomes.Where(output => output.IsError), output => Assert.StartsWith("Not enough stock", output.Content, StringComparison.Ordinal));
        Assert.Equal(0, await StockAsync(180));
    }

    [DatabaseFact]
    public async Task Cancel_order_returns_the_copies_once()
    {
        var stock = await StockAsync(190);
        var orderId = Ok(await Tools.PlaceOrderAsync(Alice, [new(190, 2)], Ct)).GetProperty("orderId").GetInt32();

        var cancelled = Ok(await Tools.CancelOrderAsync(orderId, Ct));

        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(stock, await StockAsync(190));
        Assert.Equal($"Order {orderId} is already cancelled.", Error(await Tools.CancelOrderAsync(orderId, Ct)));
        Assert.Equal(stock, await StockAsync(190));
    }

    [DatabaseFact]
    public async Task Restock_adds_copies()
    {
        var stock = await StockAsync(200);

        var restocked = Ok(await Tools.RestockBookAsync(200, 5, Ct));

        Assert.Equal(stock + 5, restocked.GetProperty("stock").GetInt32());
        Assert.Equal(stock + 5, await StockAsync(200));
    }

    [DatabaseFact]
    public async Task With_the_database_down_tools_fail_and_once_it_is_back_they_work_again()
    {
        // Several pooled connections, all of which the stop ends.
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Tools.SearchBooksAsync(cancellationToken: Ct)));
        await database.TakeDownAsync();
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => Tools.GetBookAsync(144, Ct));
            await Assert.ThrowsAnyAsync<Exception>(() => Tools.PlaceOrderAsync(Alice, [new(144, 1)], Ct));
        }
        finally
        {
            await database.BringBackAsync();
        }

        for (var call = 0; call < 5; call++)
        {
            Ok(await Tools.GetBookAsync(144, Ct));
        }

        Ok(await Tools.RestockBookAsync(144, 1, Ct));
    }
}
