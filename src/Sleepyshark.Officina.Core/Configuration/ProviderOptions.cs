namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A model provider. Later slices add retries, timeouts, features and prices (MDL-05, MDL-09).</summary>
public sealed record ProviderOptions
{
    /// <summary>The name and type of the provider the core configures by default.</summary>
    public const string ClaudeName = "claude";

    /// <summary>The default <c>claude</c> provider, which reads the secret <c>ANTHROPIC_API_KEY</c>.</summary>
    public static ProviderOptions Claude { get; } = new() { ApiKey = new SecretReference("ANTHROPIC_API_KEY") };

    [Setting("The provider implementation.", Example = "\"claude\"")]
    public string Type { get; init; } = ClaudeName;

    [Setting("The provider's credential, as the name of a secret. A literal key is rejected (CFG-09).", Example = "{ \"secret\": \"ANTHROPIC_API_KEY\" }")]
    public SecretReference? ApiKey { get; init; }

    [Setting("Overrides the provider's endpoint. Unset uses the provider's own.", Example = "\"https://llm-proxy.example.com\"")]
    public string? BaseUrl { get; init; }
}
