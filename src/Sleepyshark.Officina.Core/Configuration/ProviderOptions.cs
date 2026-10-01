namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A model provider. Later slices add retries, timeouts, features and prices.</summary>
public sealed record ProviderOptions
{
    /// <summary>The name of the provider the core configures by default.</summary>
    public const string ClaudeName = "claude";

    /// <summary>The default <c>claude</c> provider, which reads the secret <c>ANTHROPIC_API_KEY</c>.</summary>
    public static ProviderOptions Claude { get; } = new() { ApiKey = new SecretReference("ANTHROPIC_API_KEY") };

    [Setting("The provider's credential, as the name of a secret, which is read when it is used.", Example = """{ "secret": "ANTHROPIC_API_KEY" }""")]
    public SecretReference? ApiKey { get; init; }
}
