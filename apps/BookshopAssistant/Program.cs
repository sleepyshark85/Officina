// Bookshop Assistant, the reference application: a console chatbot for bookshop staff over the PostgreSQL database in
// compose.yaml, with telemetry to the compose file's dashboard and exports through its filesystem MCP server; start.sh
// starts all three. Its settings are in appsettings.json. --demo compacts and clears early enough to see in a short session.
using System.Globalization;
using BookshopAssistant;
using Microsoft.Extensions.DependencyInjection;
using Sleepyshark.Officina;

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

var demo = args.Contains("--demo") || settings.Demo;
var registrations = new ServiceCollection().AddBookshop(settings, demo).AddBookshopTelemetry(settings);
try
{
    await registrations.AddExportsAsync(settings.ExportsUrl, CancellationToken.None);
}
catch (Exception exception) when (exception is IOException or InvalidOperationException)
{
    await Console.Error.WriteLineAsync(
        $"The export server at {settings.ExportsUrl} could not be reached: {exception.Message}\n"
        + "Start it with ./start.sh (or pwsh -File start.ps1) in the application's folder, or change ExportsUrl in appsettings.Local.json.");
    return 1;
}

await using var services = registrations.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
services.StartTelemetry();
var console = services.GetRequiredService<BookshopConsole>();

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

if (demo)
{
    await Console.Out.WriteLineAsync("Demo mode: compaction from 50,000 input tokens, and old tool results cleared above 12 tool calls.");
}

await console.RunAsync(
    services.GetRequiredKeyedService<Agent>(BookshopServices.Chat), services.GetRequiredKeyedService<Agent>(BookshopServices.Summarizer));
return 0;
