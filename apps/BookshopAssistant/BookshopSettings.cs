using Microsoft.Extensions.Configuration;

namespace BookshopAssistant;

/// <summary>
/// The application's settings, from <c>appsettings.json</c> and then, if present, <c>appsettings.Local.json</c>, which
/// git ignores: the place for the API key and for this machine's changes.
/// </summary>
public sealed record BookshopSettings
{
    /// <summary>The database's connection string; the default is the compose file's database.</summary>
    public string Database { get; init; } = "";

    /// <summary>The Anthropic API key; when unset, the SDK finds credentials as usual (<c>ant auth login</c>).</summary>
    public string? AnthropicApiKey { get; init; }

    /// <summary>Where telemetry goes over OTLP: the compose file's dashboard.</summary>
    public Uri OtlpEndpoint { get; init; } = new("http://localhost:4317");

    /// <summary>The dashboard <c>/audit</c> links to.</summary>
    public Uri DashboardUrl { get; init; } = new("http://localhost:18888");

    /// <summary>The export server's MCP endpoint.</summary>
    public Uri ExportsUrl { get; init; } = new("http://localhost:18800/mcp");

    /// <summary>The folder of each staff member's memory.</summary>
    public string DataFolder { get; init; } = "data";

    /// <summary>Compacts and clears early enough to see in a short session; <c>--demo</c> also sets it.</summary>
    public bool Demo { get; init; }

    /// <summary>Puts message text and tool inputs and results in traces, for debugging.</summary>
    public bool TelemetryContent { get; init; }

    /// <summary>A lower budget per reply, in US dollars, to show a budget stop.</summary>
    public decimal? ReplyBudget { get; init; }

    /// <summary>Reads the settings files in <paramref name="folder"/>; throws <see cref="InvalidDataException"/> when they are not valid.</summary>
    public static BookshopSettings Load(string folder)
    {
        BookshopSettings? settings;
        try
        {
            settings = new ConfigurationBuilder()
                .SetBasePath(folder)
                .AddJsonFile("appsettings.json", optional: false)
                .AddJsonFile("appsettings.Local.json", optional: true)
                .Build()
                .Get<BookshopSettings>(options => options.ErrorOnUnknownConfiguration = true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or IOException)
        {
            throw new InvalidDataException($"The settings in {folder} are not valid: {exception.Message}", exception);
        }

        settings ??= new();
        if (string.IsNullOrWhiteSpace(settings.Database))
        {
            throw new InvalidDataException("Set Database, the connection string, in appsettings.json.");
        }

        if (settings.ReplyBudget is <= 0)
        {
            throw new InvalidDataException($"ReplyBudget must be a positive amount in US dollars, such as 0.01, not {settings.ReplyBudget}.");
        }

        // The binder takes any text as a relative URI, such as one missing its scheme.
        foreach (var (name, url) in new[] { (nameof(OtlpEndpoint), settings.OtlpEndpoint), (nameof(DashboardUrl), settings.DashboardUrl), (nameof(ExportsUrl), settings.ExportsUrl) })
        {
            if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            {
                throw new InvalidDataException($"{name} must be an absolute http URL, such as http://localhost:18800/mcp, not \"{url.OriginalString}\".");
            }
        }

        return settings;
    }
}
