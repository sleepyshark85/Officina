using System.Collections.Immutable;
using System.ComponentModel;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Sleepyshark.Officina;

namespace BookshopAssistant;

/// <summary>A line of an order to place: which book, how many copies.</summary>
public sealed record OrderLine(
    [property: Description("The book's id.")] int BookId,
    [property: Description("How many copies, at least 1.")] int Quantity);

/// <summary>
/// The bookshop's nine tools (APP-05, APP-06) over PostgreSQL. Every query is a fixed, parameterized constant (APP-08):
/// the model gives values, never SQL. A business rule failure, such as not enough stock, is an error result (APP-07); a
/// database that cannot be reached throws, which the core turns into an error result too (APP-18).
/// </summary>
public sealed class BookshopTools(NpgsqlDataSource database)
{
    /// <summary>The most books one search returns: enough for a broad search of about 15k tokens (APP-17, TOOL-06).</summary>
    public const int MaxSearchResults = 400;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ImmutableArray<Tool> All =>
    [
        Tool.FromFunction(
            "search_books",
            $"Searches the catalogue. Every filter is optional; text filters match part of the title or author name, ignoring case. Returns books cheapest first, at most {MaxSearchResults}.",
            ToolKind.Read,
            SearchBooksAsync),
        Tool.FromFunction("get_book", "Gets one book with its author, genre, price, year and copies in stock.", ToolKind.Read, GetBookAsync),
        Tool.FromFunction("find_customer", "Finds customers whose name or email contains the given text, ignoring case.", ToolKind.Read, FindCustomerAsync),
        Tool.FromFunction("list_customer_orders", "Lists a customer's orders, newest first, with status and total.", ToolKind.Read, ListCustomerOrdersAsync),
        Tool.FromFunction("get_order", "Gets one order with its customer, status, lines and total.", ToolKind.Read, GetOrderAsync),
        Tool.FromFunction("add_customer", "Adds a new customer. Emails are unique.", ToolKind.Write, AddCustomerAsync, needsApproval: true),
        Tool.FromFunction(
            "place_order",
            "Places an order for a customer at the books' current prices, taking the copies from stock. Fails, changing nothing, if a book is unknown or has too few copies in stock.",
            ToolKind.Write,
            PlaceOrderAsync,
            needsApproval: true),
        Tool.FromFunction("cancel_order", "Cancels a placed order and returns its copies to stock.", ToolKind.Write, CancelOrderAsync, needsApproval: true),
        Tool.FromFunction("restock_book", "Adds copies of a book to its stock.", ToolKind.Write, RestockBookAsync, needsApproval: true),
    ];

    private const string SearchBooksSql = """
        select b.id, b.title, a.name, g.name, b.price, s.quantity
        from books b
        join authors a on a.id = b.author_id
        join genres g on g.id = b.genre_id
        join stock s on s.book_id = b.id
        where (@title is null or strpos(lower(b.title), lower(@title)) > 0)
          and (@author is null or strpos(lower(a.name), lower(@author)) > 0)
          and (@genre is null or lower(g.name) = lower(@genre))
          and (@max_price is null or b.price <= @max_price)
          and (not @in_stock or s.quantity > 0)
        order by b.price, b.title
        limit @limit
        """;

    public async Task<ToolOutput> SearchBooksAsync(
        [Description("Part of the title.")] string? title = null,
        [Description("Part of the author's name.")] string? author = null,
        [Description("The genre: Fantasy, Science Fiction, Mystery, Romance, History, Biography, Poetry, Horror, Children, Cookery, Travel or Philosophy.")] string? genre = null,
        [Description("The highest price.")] decimal? maxPrice = null,
        [Description("Only books with copies in stock.")] bool inStock = false,
        [Description("The most books to return, 20 if not given.")] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand(SearchBooksSql);
        command.Parameters.Add(Text("title", title));
        command.Parameters.Add(Text("author", author));
        command.Parameters.Add(Text("genre", genre));
        command.Parameters.Add(new NpgsqlParameter("max_price", NpgsqlDbType.Numeric) { Value = (object?)maxPrice ?? DBNull.Value });
        command.Parameters.AddWithValue("in_stock", inStock);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, MaxSearchResults));
        var books = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            books.Add(new { id = reader.GetInt32(0), title = reader.GetString(1), author = reader.GetString(2), genre = reader.GetString(3), price = reader.GetDecimal(4), stock = reader.GetInt32(5) });
        }

        return Ok(books);
    }

    private const string GetBookSql = """
        select b.id, b.title, a.name, g.name, b.price, b.published_year, s.quantity
        from books b
        join authors a on a.id = b.author_id
        join genres g on g.id = b.genre_id
        join stock s on s.book_id = b.id
        where b.id = @id
        """;

    public async Task<ToolOutput> GetBookAsync([Description("The book's id.")] int bookId, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand(GetBookSql);
        command.Parameters.AddWithValue("id", bookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Ok(new { id = reader.GetInt32(0), title = reader.GetString(1), author = reader.GetString(2), genre = reader.GetString(3), price = reader.GetDecimal(4), year = reader.GetInt32(5), stock = reader.GetInt32(6) })
            : Error($"There is no book with id {bookId}.");
    }

    private const string FindCustomerSql = """
        select id, name, email
        from customers
        where strpos(lower(name), lower(@text)) > 0 or strpos(lower(email), lower(@text)) > 0
        order by name, id
        limit 20
        """;

    public async Task<ToolOutput> FindCustomerAsync(
        [Description("Part of the customer's name or email.")] string nameOrEmail, CancellationToken cancellationToken = default)
    {
        await using var command = database.CreateCommand(FindCustomerSql);
        command.Parameters.AddWithValue("text", nameOrEmail);
        var customers = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            customers.Add(new { id = reader.GetInt32(0), name = reader.GetString(1), email = reader.GetString(2) });
        }

        return Ok(customers);
    }

    private const string CustomerExistsSql = "select name from customers where id = @id";

    private const string ListCustomerOrdersSql = """
        select o.id, o.status, o.placed_at, o.total, (select sum(quantity) from order_lines l where l.order_id = o.id)
        from orders o
        where o.customer_id = @id
        order by o.placed_at desc, o.id desc
        """;

    public async Task<ToolOutput> ListCustomerOrdersAsync([Description("The customer's id.")] int customerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        if (await CustomerNameAsync(connection, null, customerId, cancellationToken) is null)
        {
            return Error($"There is no customer with id {customerId}.");
        }

        await using var command = new NpgsqlCommand(ListCustomerOrdersSql, connection);
        command.Parameters.AddWithValue("id", customerId);
        var orders = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            orders.Add(new { id = reader.GetInt32(0), status = reader.GetString(1), placedAt = reader.GetDateTime(2), total = reader.GetDecimal(3), copies = reader.GetInt64(4) });
        }

        return Ok(orders);
    }

    private const string GetOrderSql = """
        select o.id, o.customer_id, c.name, o.status, o.placed_at, o.total
        from orders o
        join customers c on c.id = o.customer_id
        where o.id = @id
        """;

    private const string GetOrderLinesSql = """
        select l.book_id, b.title, l.quantity, l.unit_price
        from order_lines l
        join books b on b.id = l.book_id
        where l.order_id = @id
        order by l.book_id
        """;

    public async Task<ToolOutput> GetOrderAsync([Description("The order's id.")] int orderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(GetOrderSql, connection);
        command.Parameters.AddWithValue("id", orderId);
        object order;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Error($"There is no order with id {orderId}.");
            }

            order = new { id = reader.GetInt32(0), customerId = reader.GetInt32(1), customer = reader.GetString(2), status = reader.GetString(3), placedAt = reader.GetDateTime(4), total = reader.GetDecimal(5) };
        }

        await using var lines = new NpgsqlCommand(GetOrderLinesSql, connection);
        lines.Parameters.AddWithValue("id", orderId);
        return Ok(new { order, lines = await ReadLinesAsync(lines, cancellationToken) });
    }

    private const string AddCustomerSql = """
        insert into customers (name, email) values (@name, @email)
        on conflict (email) do nothing
        returning id
        """;

    public async Task<ToolOutput> AddCustomerAsync(
        [Description("The customer's full name.")] string name, [Description("The customer's email.")] string email,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            return Error("A customer needs a name and an email.");
        }

        await using var command = database.CreateCommand(AddCustomerSql);
        command.Parameters.AddWithValue("name", name.Trim());
        command.Parameters.AddWithValue("email", email.Trim());
        return await command.ExecuteScalarAsync(cancellationToken) is int id
            ? Ok(new { id, name = name.Trim(), email = email.Trim() })
            : Error($"A customer with the email {email.Trim()} already exists.");
    }

    private const string LockStockSql = """
        select b.id, b.title, b.price, s.quantity
        from books b
        join stock s on s.book_id = b.id
        where b.id = any(@ids)
        order by b.id
        for update of s
        """;

    private const string TakeStockSql = "update stock set quantity = quantity - @quantity where book_id = @id";

    private const string InsertOrderSql = "insert into orders (customer_id, status, total) values (@customer, 'placed', @total) returning id";

    private const string InsertOrderLineSql = "insert into order_lines (order_id, book_id, quantity, unit_price) values (@order, @book, @quantity, @price)";

    public async Task<ToolOutput> PlaceOrderAsync(
        [Description("The customer's id.")] int customerId, [Description("The books and copies to order, each book once.")] OrderLine[] lines,
        CancellationToken cancellationToken = default)
    {
        if (lines.Length == 0 || lines.Any(line => line.Quantity < 1) || lines.DistinctBy(line => line.BookId).Count() != lines.Length)
        {
            return Error("An order needs at least one line, each book once, each for at least one copy.");
        }

        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var customer = await CustomerNameAsync(connection, transaction, customerId, cancellationToken);
        if (customer is null)
        {
            return Error($"There is no customer with id {customerId}.");
        }

        // Locks the books' stock rows, in id order so concurrent orders cannot deadlock, until the order commits.
        var books = new Dictionary<int, (string Title, decimal Price, int Stock)>();
        await using (var command = new NpgsqlCommand(LockStockSql, connection, transaction))
        {
            command.Parameters.AddWithValue("ids", lines.Select(line => line.BookId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                books[reader.GetInt32(0)] = (reader.GetString(1), reader.GetDecimal(2), reader.GetInt32(3));
            }
        }

        foreach (var line in lines)
        {
            if (!books.TryGetValue(line.BookId, out var book))
            {
                return Error($"There is no book with id {line.BookId}. Nothing was ordered.");
            }

            if (book.Stock < line.Quantity)
            {
                return Error($"Not enough stock for \"{book.Title}\" (id {line.BookId}): {line.Quantity} requested, {book.Stock} in stock. Nothing was ordered.");
            }
        }

        var total = lines.Sum(line => books[line.BookId].Price * line.Quantity);
        int orderId;
        await using (var command = new NpgsqlCommand(InsertOrderSql, connection, transaction))
        {
            command.Parameters.AddWithValue("customer", customerId);
            command.Parameters.AddWithValue("total", total);
            orderId = (int)(await command.ExecuteScalarAsync(cancellationToken))!;
        }

        foreach (var line in lines)
        {
            await using var take = new NpgsqlCommand(TakeStockSql, connection, transaction);
            take.Parameters.AddWithValue("id", line.BookId);
            take.Parameters.AddWithValue("quantity", line.Quantity);
            await take.ExecuteNonQueryAsync(cancellationToken);

            await using var insert = new NpgsqlCommand(InsertOrderLineSql, connection, transaction);
            insert.Parameters.AddWithValue("order", orderId);
            insert.Parameters.AddWithValue("book", line.BookId);
            insert.Parameters.AddWithValue("quantity", line.Quantity);
            insert.Parameters.AddWithValue("price", books[line.BookId].Price);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return Ok(new
        {
            orderId,
            customerId,
            customer,
            lines = lines.Select(line => new { bookId = line.BookId, title = books[line.BookId].Title, quantity = line.Quantity, unitPrice = books[line.BookId].Price }),
            total,
        });
    }

    private const string LockOrderSql = "select status from orders where id = @id for update";

    private const string ReturnStockSql = """
        update stock s set quantity = s.quantity + l.quantity
        from order_lines l
        where l.order_id = @id and s.book_id = l.book_id
        """;

    private const string CancelOrderSql = "update orders set status = 'cancelled' where id = @id";

    public async Task<ToolOutput> CancelOrderAsync([Description("The order's id.")] int orderId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(LockOrderSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", orderId);
            switch (await command.ExecuteScalarAsync(cancellationToken))
            {
                case null:
                    return Error($"There is no order with id {orderId}.");
                case "cancelled":
                    return Error($"Order {orderId} is already cancelled.");
            }
        }

        await using (var command = new NpgsqlCommand(ReturnStockSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", orderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand(CancelOrderSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", orderId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var lines = new NpgsqlCommand(GetOrderLinesSql, connection, transaction);
        lines.Parameters.AddWithValue("id", orderId);
        var returned = await ReadLinesAsync(lines, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { orderId, status = "cancelled", returnedToStock = returned });
    }

    private const string RestockBookSql = """
        update stock s set quantity = s.quantity + @quantity
        from books b
        where s.book_id = @id and b.id = s.book_id
        returning b.title, s.quantity
        """;

    public async Task<ToolOutput> RestockBookAsync(
        [Description("The book's id.")] int bookId, [Description("How many copies to add, at least 1.")] int quantity,
        CancellationToken cancellationToken = default)
    {
        if (quantity < 1)
        {
            return Error("Restock at least one copy.");
        }

        await using var command = database.CreateCommand(RestockBookSql);
        command.Parameters.AddWithValue("id", bookId);
        command.Parameters.AddWithValue("quantity", quantity);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Ok(new { bookId, title = reader.GetString(0), stock = reader.GetInt32(1) })
            : Error($"There is no book with id {bookId}.");
    }

    private static async Task<string?> CustomerNameAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, int customerId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(CustomerExistsSql, connection, transaction);
        command.Parameters.AddWithValue("id", customerId);
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<List<object>> ReadLinesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var lines = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(new { bookId = reader.GetInt32(0), title = reader.GetString(1), quantity = reader.GetInt32(2), unitPrice = reader.GetDecimal(3) });
        }

        return lines;
    }

    private static NpgsqlParameter Text(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value };

    private static ToolOutput Ok(object value) => new(JsonSerializer.Serialize(value, Json));

    private static ToolOutput Error(string message) => new(message, IsError: true);
}
