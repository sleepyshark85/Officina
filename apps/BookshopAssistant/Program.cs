// Bookshop Assistant, the reference application (REQUIREMENTS §2, ARCHITECTURE §12): a console chatbot for bookshop
// staff over the PostgreSQL database in compose.yaml. Needs BOOKSHOP_CONNECTION_STRING and ANTHROPIC_API_KEY. Traces,
// metrics and logs go over OTLP to the dashboard in compose.yaml (APP-20): OTEL_EXPORTER_OTLP_ENDPOINT, by default
// http://localhost:4317; BOOKSHOP_DASHBOARD_URL is where /audit links to, by default http://localhost:18888. Exports go to
// the exports folder, through the filesystem MCP server the compose file runs in Docker (APP-12). Demo mode (APP-17), with
// --demo or BOOKSHOP_DEMO=1, compacts and clears old tool results early enough to see in a short session.
using BookshopAssistant;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Claude;
using Sleepyshark.Officina.Mcp;

var connectionString = Environment.GetEnvironmentVariable("BOOKSHOP_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync(
        "Set BOOKSHOP_CONNECTION_STRING, for the compose database: Host=localhost;Port=5432;Username=bookshop;Password=shelf-demo-41;Database=bookshop");
    return 1;
}

var dashboard = new Uri(Environment.GetEnvironmentVariable("BOOKSHOP_DASHBOARD_URL") is { Length: > 0 } url ? url : "http://localhost:18888");
var resource = ResourceBuilder.CreateDefault().AddService("bookshop-assistant");
using var tracing = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(Telemetry.SourceName, BookshopConsole.SourceName)
    .AddOtlpExporter()
    .Build();
using var metrics = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(Telemetry.SourceName)
    .AddOtlpExporter((_, reader) => reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000)
    .Build();
using var logging = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(resource);
    options.AddOtlpExporter();
}));

var demo = args.Contains("--demo") || Environment.GetEnvironmentVariable("BOOKSHOP_DEMO") == "1";
await using var database = NpgsqlDataSource.Create(connectionString);
using var model = new ClaudeModel
{
    Model = "claude-opus-5-5",
    Effort = ClaudeEffort.Medium,
    MaxOutputTokens = 16_000,

    // A demo is one sitting, and its large searches would cost 60% more to cache for an hour.
    CacheLifetime = demo ? CacheLifetime.FiveMinutes : CacheLifetime.OneHour,
};
using var summaryModel = new ClaudeModel { Model = "claude-opus-5-5", Effort = ClaudeEffort.Low, MaxOutputTokens = 4_000 };
var audit = new AuditTable(database);
var console = new BookshopConsole(
    Console.In, Console.Out, TimeProvider.System, echoInput: Console.IsInputRedirected, audit, new SessionStore(database), dashboard,
    logging.CreateLogger<BookshopConsole>());

// Ctrl+C stops the reply in progress and the session goes on (APP-03); with no reply in progress, it quits.
// An exception here would end the process, so none escapes.
Console.CancelKeyPress += (_, press) =>
{
    try
    {
        press.Cancel = console.CancelReply();
    }
#pragma warning disable CA1031 // A failed cancel must not end the application (APP-03).
    catch (Exception)
#pragma warning restore CA1031
    {
        press.Cancel = true;
    }
};

// The export server (APP-12), from the compose file in the current folder, or BOOKSHOP_COMPOSE_FILE.
var composeFile = Environment.GetEnvironmentVariable("BOOKSHOP_COMPOSE_FILE") is { Length: > 0 } file ? file : "compose.yaml";
McpToolSource exports;
try
{
    exports = await Exports.ConnectAsync(Exports.Server(composeFile), CancellationToken.None);
}
catch (Exception exception) when (exception is IOException or InvalidOperationException)
{
    await Console.Error.WriteLineAsync(
        $"The export server (the filesystem service of {composeFile}) could not be started: {exception.Message}\n"
        + "It runs in Docker: check that Docker is running, that its image is pulled (docker compose --profile mcp pull), "
        + "and that this is the folder of compose.yaml, or set BOOKSHOP_COMPOSE_FILE.");
    return 1;
}

await using var stopExports = exports;

var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
var agent = BookshopAgent.Create(
    model, new BookshopTools(database), console, audit, password is null ? [] : [password], TimeProvider.System, exports.Tools, demo);
if (demo)
{
    await Console.Out.WriteLineAsync("Demo mode: compaction from 50,000 input tokens, and old tool results cleared after 4 tool calls.");
}

await console.RunAsync(agent, SessionSummarizer.Create(summaryModel, TimeProvider.System));
return 0;
