using Sleepyshark.Officina.Mcp;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// TEST-09 for APP-12: the console exports a customer's order history as CSV through the real filesystem MCP server,
/// started by the compose file's service in Docker, writing into a folder of the test's own. Only the model and the
/// staff member are scripted.
/// </summary>
public sealed class ExportTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>, IAsyncLifetime
{
    private const string FilePath = "/projects/exports/order-history-alice-martin.csv";

    private readonly string folder = Directory.CreateTempSubdirectory("bookshop-exports-").FullName;
    private McpToolSource? exports;

    public async ValueTask InitializeAsync()
    {
        if (BookshopDatabase.Available)
        {
            exports = await Exports.ConnectAsync(Exports.Server(Path.Combine(AppContext.BaseDirectory, "compose.yaml"), folder), TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (exports is not null)
        {
            await exports.DisposeAsync();
        }

        Directory.Delete(folder, recursive: true);
    }

    [DatabaseFact]
    public void APP_12_the_allow_list_offers_only_writing_a_file_with_approval_and_listing_the_folder()
    {
        Assert.Equal(["filesystem__write_file", "filesystem__list_directory"], exports!.Tools.Select(tool => tool.Name));
        Assert.Equal([true, false], exports.Tools.Select(tool => tool.NeedsApproval));
    }

    [DatabaseFact]
    public async Task APP_12_an_order_history_is_exported_as_csv_after_approval()
    {
        var csv = await OrderHistoryCsvAsync(1);
        var model = Model()
            .Reply(SayThenCall("Let me find Alice.", Call("c1", "find_customer", new { nameOrEmail = "Alice Martin" })))
            .Reply(SayThenCall("Reading her orders.", Call("c2", "list_customer_orders", new { customerId = 1 })))
            .Reply(SayThenCall("I'll write the file.", Call("c3", "filesystem__write_file", new { path = FilePath, content = csv })))
            .Reply(SayThenCall("Checking the folder.", Call("c4", "filesystem__list_directory", new { path = "/projects/exports" })))
            .Reply("Exported to order-history-alice-martin.csv.");

        var transcript = await RunAsync(database, model, ["Sam", "Export Alice Martin's order history as CSV.", "y", "/quit"], exportTools: exports!.Tools);

        InOrder(
            transcript,
            "  > list_customer_orders {\"customerId\":1}",
            "  ? filesystem__write_file needs your approval. Its exact input:",
            "    Approve? [y/N] y",
            "  < filesystem__write_file: ok",
            "  < filesystem__list_directory: ok",
            "Exported to order-history-alice-martin.csv.");
        Assert.Equal(csv, await File.ReadAllTextAsync(Path.Combine(folder, "order-history-alice-martin.csv"), TestContext.Current.CancellationToken));
        Assert.Equal("[FILE] order-history-alice-martin.csv", LastResults(model)[0].Content);
    }

    [DatabaseFact]
    public async Task APP_12_a_declined_export_writes_no_file()
    {
        var model = Model()
            .Reply(SayThenCall("I'll write the file.", Call("c1", "filesystem__write_file", new { path = FilePath, content = "id\n" })))
            .Reply("Understood, no file.");

        var transcript = await RunAsync(database, model, ["Sam", "Export it.", "n", "/quit"], exportTools: exports!.Tools);

        InOrder(transcript, "    Approve? [y/N] n", "  < filesystem__write_file: error: The call was denied: the staff member declined", "Understood, no file.");
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    /// <summary>The CSV the model would write from the customer's orders.</summary>
    private async Task<string> OrderHistoryCsvAsync(int customerId)
    {
        await using var command = database.DataSource.CreateCommand(
            "select string_agg(format('%s,%s,%s,%s', id, status, to_char(placed_at at time zone 'UTC', 'YYYY-MM-DD'), total), E'\\n' order by placed_at desc, id desc) from orders where customer_id = $1");
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = customerId });
        return $"order_id,status,placed_on,total\n{await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)}\n";
    }
}
