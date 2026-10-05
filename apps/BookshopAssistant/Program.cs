// Bookshop Assistant, the reference application (REQUIREMENTS §2, ARCHITECTURE §12): a console chatbot for bookshop
// staff over the PostgreSQL database in compose.yaml. Needs BOOKSHOP_CONNECTION_STRING and ANTHROPIC_API_KEY.
using BookshopAssistant;
using Npgsql;
using Sleepyshark.Officina.Claude;

var connectionString = Environment.GetEnvironmentVariable("BOOKSHOP_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync(
        "Set BOOKSHOP_CONNECTION_STRING, for the compose database: Host=localhost;Port=5432;Username=bookshop;Password=bookshop;Database=bookshop");
    return 1;
}

await using var database = NpgsqlDataSource.Create(connectionString);
using var model = new ClaudeModel
{
    Model = "claude-opus-5-5",
    Effort = ClaudeEffort.Medium,
    MaxOutputTokens = 16_000,
    CacheLifetime = CacheLifetime.OneHour,
};
var console = new BookshopConsole(Console.In, Console.Out, TimeProvider.System, echoInput: Console.IsInputRedirected);

// Ctrl+C stops the reply in progress and the session goes on (APP-03); with no reply in progress, it quits.
Console.CancelKeyPress += (_, press) => press.Cancel = console.CancelReply();

var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
var agent = BookshopAgent.Create(model, new BookshopTools(database), console, password is null ? [] : [password]);
await console.RunAsync(agent);
return 0;
