using Sleepyshark.Officina.Mcp;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// The console exports a customer's order history as CSV through the real filesystem MCP server, started by the compose
/// file's service in Docker and writing into the test's own folder. Only the model and the staff member are scripted.
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

    [DatabaseFact]
    public async Task APP_12_APP_03_the_server_runs_out_of_ctrl_c_s_reach_and_a_reply_after_it_dies_starts_it_again()
    {
        var model = Model()
            .Reply(SayThenCall("Checking.", Call("c1", "filesystem__list_directory", new { path = "/projects/exports" })))
            .Reply("The folder is empty.")
            .Reply(SayThenCall("Checking again.", Call("c2", "filesystem__list_directory", new { path = "/projects/exports" })))
            .Reply("Still empty.");

        // Between the replies, the server's process group gets the SIGINT a Ctrl+C sends to the terminal's foreground group.
        var transcript = await RunAsync(database, model, ["Sam", "What is in the exports folder?", (Func<Task>)InterruptServerAsync, "And now?", "/quit"], exportTools: exports!.Tools);

        InOrder(transcript, "  < filesystem__list_directory: ok", "The folder is empty.", "you> And now?", "  < filesystem__list_directory: ok", "Still empty.");
        var session = SessionId(transcript);
        var changes = (await new AuditTable(database.DataSource).ReadAsync(session, TestContext.Current.CancellationToken))
            .Where(entry => entry.Kind == Sleepyshark.Officina.AuditKind.ToolSource).Select(entry => entry.Outcome);
        Assert.Equal(["connected", "disconnected", "connected"], changes);
    }

    /// <summary>
    /// Sends SIGINT to the server's process group, found by the test's folder in their command line, after checking they
    /// are in another session than this process, which a Ctrl+C in this terminal would reach.
    /// </summary>
    private async Task InterruptServerAsync()
    {
        var own = Stat(Environment.ProcessId);
        var server = Directory.EnumerateDirectories("/proc")
            .Select(path => int.TryParse(Path.GetFileName(path), out var id) ? id : 0)
            .Where(id => id > 0 && ReadOrEmpty($"/proc/{id}/cmdline").Contains(folder, StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(server);
        var group = Stat(server[0]).Group;
        Assert.All(server, id => Assert.NotEqual(own.Session, Stat(id).Session));

        using var kill = System.Diagnostics.Process.Start("kill", ["-INT", "--", $"-{group}"]);
        await kill.WaitForExitAsync(TestContext.Current.CancellationToken);
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (server.Any(id => Directory.Exists($"/proc/{id}") && Stat(id).State != "Z"))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), "The server's processes were still running 10 s after the interrupt.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        static string ReadOrEmpty(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return "";
            }
        }
    }

    /// <summary>A process's state, process group and session, from <c>/proc/&lt;id&gt;/stat</c> after the command's closing parenthesis.</summary>
    private static (string State, int Group, int Session) Stat(int processId)
    {
        string text;
        try
        {
            text = File.ReadAllText($"/proc/{processId}/stat");
        }
        catch (IOException)
        {
            return ("Z", 0, 0);
        }

        var fields = text[(text.LastIndexOf(')') + 2)..].Split(' ');
        return (fields[0], int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture), int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture));
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
