// Bookshop Assistant, the reference application (REQUIREMENTS §2, ARCHITECTURE §12): a console chatbot for bookshop
// staff over the PostgreSQL database in compose.yaml. Needs BOOKSHOP_CONNECTION_STRING and ANTHROPIC_API_KEY. Traces,
// metrics and logs go over OTLP to the dashboard in compose.yaml (APP-20): OTEL_EXPORTER_OTLP_ENDPOINT, by default
// http://localhost:4317; BOOKSHOP_DASHBOARD_URL is where /audit links to, by default http://localhost:18888.
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

await using var database = NpgsqlDataSource.Create(connectionString);
using var model = new ClaudeModel
{
    Model = "claude-opus-5-5",
    Effort = ClaudeEffort.Medium,
    MaxOutputTokens = 16_000,
    CacheLifetime = CacheLifetime.OneHour,
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

var password = new NpgsqlConnectionStringBuilder(connectionString).Password;
var agent = BookshopAgent.Create(model, new BookshopTools(database), console, audit, password is null ? [] : [password], TimeProvider.System);
await console.RunAsync(agent, SessionSummarizer.Create(summaryModel, TimeProvider.System));
return 0;
