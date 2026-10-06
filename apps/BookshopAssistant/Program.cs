// Bookshop Assistant, the reference application: a console chatbot for bookshop staff over the PostgreSQL database in
// compose.yaml, with telemetry to the compose file's dashboard and exports through its filesystem MCP server; start.sh
// starts all three. Its settings are in appsettings.json. --demo compacts and clears early enough to see in a short session.
using System.Globalization;
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

BookshopSettings settings;
try
{
    settings = BookshopSettings.Load(AppContext.BaseDirectory);
}
catch (InvalidDataException exception)
{
    await Console.Error.WriteLineAsync(exception.Message);
    return 1;
}

if (settings.ReplyBudget is { } replyBudget)
{
    await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Reply budget: ${replyBudget}."));
}

var resource = ResourceBuilder.CreateDefault().AddService("bookshop-assistant");
using var tracing = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resource)
    .AddSource(Telemetry.SourceName, BookshopConsole.SourceName)
    .AddOtlpExporter(options => options.Endpoint = settings.OtlpEndpoint)
    .Build();
using var metrics = Sdk.CreateMeterProviderBuilder()
    .SetResourceBuilder(resource)
    .AddMeter(Telemetry.SourceName)
    .AddOtlpExporter((exporter, reader) =>
    {
        exporter.Endpoint = settings.OtlpEndpoint;
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000;
    })
    .Build();
using var logging = LoggerFactory.Create(builder => builder.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(resource);
    options.AddOtlpExporter(exporter => exporter.Endpoint = settings.OtlpEndpoint);
}));

var demo = args.Contains("--demo") || settings.Demo;
await using var database = NpgsqlDataSource.Create(settings.Database);
using var model = BookshopAgent.Model(demo, settings.AnthropicApiKey);
using var summaryModel = new ClaudeModel(settings.AnthropicApiKey) { Model = ClaudeModel.Opus55, Effort = ClaudeEffort.Low, MaxOutputTokens = 4_000 };
var audit = new AuditTable(database);
var memory = new FileMemoryStore(Path.Combine(settings.DataFolder, "memory"));
var console = new BookshopConsole(
    Console.In, Console.Out, TimeProvider.System, echoInput: Console.IsInputRedirected, audit, new SessionStore(database), settings.DashboardUrl,
    logging.CreateLogger<BookshopConsole>(), settings.ReplyBudget is { } reply ? Budgets.Default with { Reply = reply } : null, memory);

// Ctrl+C stops the reply in progress and the session goes on; with no reply in progress, it quits. No exception may
// escape here, as it would end the process.
Console.CancelKeyPress += (_, press) =>
{
    try
    {
        press.Cancel = console.CancelReply();
    }
#pragma warning disable CA1031 // A failed cancel must not end the application.
    catch (Exception)
#pragma warning restore CA1031
    {
        press.Cancel = true;
    }
};

McpToolSource exports;
try
{
    exports = await Exports.ConnectAsync(Exports.Server(settings.ExportsUrl), CancellationToken.None);
}
catch (Exception exception) when (exception is IOException or InvalidOperationException)
{
    await Console.Error.WriteLineAsync(
        $"The export server at {settings.ExportsUrl} could not be reached: {exception.Message}\n"
        + "Start it with ./start.sh (or pwsh -File start.ps1) in the application's folder, or change ExportsUrl in appsettings.json.");
    return 1;
}

await using var stopExports = exports;

var password = new NpgsqlConnectionStringBuilder(settings.Database).Password;
var agent = BookshopAgent.Create(
    model, new BookshopTools(database), memory, console, audit, password is null ? [] : [password], TimeProvider.System, exports.Tools, demo);
if (settings.TelemetryContent)
{
    agent = agent with { TelemetryContent = true };
}

if (demo)
{
    await Console.Out.WriteLineAsync("Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 tool calls.");
}

await console.RunAsync(agent, SessionSummarizer.Create(summaryModel, TimeProvider.System));
return 0;
