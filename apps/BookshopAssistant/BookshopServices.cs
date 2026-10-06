using Microsoft.Extensions.DependencyInjection;
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

namespace BookshopAssistant;

/// <summary>
/// The application's services. The chat and summarizer agents, and their models, are keyed by <see cref="Chat"/> and
/// <see cref="Summarizer"/>. Each package registers its own services; the export server, which is connected before the
/// container is built, is registered by <see cref="Exports.AddExportsAsync"/>.
/// </summary>
public static class BookshopServices
{
    public const string Chat = "chat";
    public const string Summarizer = "summarizer";

    /// <summary>Everything but telemetry's exporters, which <see cref="AddBookshopTelemetry"/> adds.</summary>
    public static IServiceCollection AddBookshop(this IServiceCollection services, BookshopSettings settings, bool demo)
    {
        ArgumentNullException.ThrowIfNull(settings);
        services.AddSingleton(settings);
        services.AddSingleton(new Terminal(Console.In, Console.Out, EchoInput: Console.IsInputRedirected));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(settings.ReplyBudget is { } reply ? Budgets.Default with { Reply = reply } : Budgets.Default);
        services.AddSingleton(_ => NpgsqlDataSource.Create(settings.Database));
        services.AddSingleton<AuditTable>();
        services.AddSingleton<SessionStore>();
        services.AddSingleton<BookshopTools>();
        services.AddFileMemoryStore(Path.Combine(settings.DataFolder, "memory"));
        services.AddSingleton<BookshopConsole>();
        services.AddSingleton<IApprover>(provider => provider.GetRequiredService<BookshopConsole>());
        services.AddLogging();

        services.AddClaudeModel(Chat, _ => BookshopAgent.Model(demo, settings.AnthropicApiKey));
        services.AddClaudeModel(Summarizer, _ => SessionSummarizer.Model(settings.AnthropicApiKey));
        services.AddKeyedSingleton(Chat, (provider, _) =>
        {
            // The database password is a secret to redact; the other parts of the connection string are not.
            var password = new NpgsqlConnectionStringBuilder(settings.Database).Password;
            var agent = BookshopAgent.Create(
                provider.GetRequiredKeyedService<IModel>(Chat), provider.GetRequiredService<BookshopTools>(), provider.GetRequiredService<IMemoryStore>(),
                provider.GetRequiredService<IApprover>(), provider.GetRequiredService<AuditTable>(), password is null ? [] : [password],
                provider.GetRequiredService<TimeProvider>(), provider.GetKeyedService<McpToolSource>(Exports.Name)?.Tools, demo);
            return settings.TelemetryContent ? agent with { TelemetryContent = true } : agent;
        });
        services.AddKeyedSingleton(Summarizer, (provider, _) =>
            SessionSummarizer.Create(provider.GetRequiredKeyedService<IModel>(Summarizer), provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Traces, metrics and logs over OTLP to the settings' endpoint; <see cref="StartTelemetry"/> starts them.</summary>
    public static IServiceCollection AddBookshopTelemetry(this IServiceCollection services, BookshopSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService("bookshop-assistant"))
            .WithTracing(tracing => tracing
                .AddSource(Telemetry.SourceName, BookshopConsole.SourceName)
                .AddOtlpExporter(exporter => exporter.Endpoint = settings.OtlpEndpoint))
            .WithMetrics(metrics => metrics
                .AddMeter(Telemetry.SourceName)
                .AddOtlpExporter((exporter, reader) =>
                {
                    exporter.Endpoint = settings.OtlpEndpoint;
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5_000;
                }))
            .WithLogging(logging => logging.AddOtlpExporter(exporter => exporter.Endpoint = settings.OtlpEndpoint));
        return services;
    }

    /// <summary>
    /// Starts exporting traces and metrics. Without a host, which would start them, they begin when first resolved.
    /// </summary>
    public static void StartTelemetry(this IServiceProvider services)
    {
        services.GetRequiredService<TracerProvider>();
        services.GetRequiredService<MeterProvider>();
    }
}
