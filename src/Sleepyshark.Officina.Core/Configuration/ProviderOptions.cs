namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A model provider. Later slices add retries, timeouts and features.</summary>
public sealed record ProviderOptions
{
    /// <summary>The name of the provider the core configures by default.</summary>
    public const string ClaudeName = "claude";

    /// <summary>
    /// The default <c>claude</c> provider, which reads the secret <c>ANTHROPIC_API_KEY</c> and ships the prices of current
    /// Claude models, as of 2026-10 (MDL-09). A new one each time: the
    /// configuration binder changes the default it binds into, which must not carry over to another load.
    /// </summary>
    public static ProviderOptions Claude => new()
    {
        ApiKey = new SecretReference("ANTHROPIC_API_KEY"),
        Prices = new Dictionary<string, ModelPrice>
        {
            ["claude-fable-5-1"] = new() { Input = 10m, Output = 50m, CacheRead = 0.25m, CacheWrite5m = 12.5m, CacheWrite1h = 20m },
            ["claude-opus-5-5"] = new() { Input = 4m, Output = 20m, CacheRead = 0.2m, CacheWrite5m = 5m, CacheWrite1h = 8m },
            ["claude-opus-5"] = new() { Input = 5m, Output = 25m, CacheRead = 0.5m, CacheWrite5m = 6.25m, CacheWrite1h = 10m },
            ["claude-sonnet-5-5"] = new() { Input = 2m, Output = 10m, CacheRead = 0.2m, CacheWrite5m = 2.5m, CacheWrite1h = 4m },
            ["claude-haiku-4-5"] = new() { Input = 1m, Output = 5m, CacheRead = 0.1m, CacheWrite5m = 1.25m, CacheWrite1h = 2m },
        },
    };

    [Setting("The provider's credential, as the name of a secret, which is read when it is used.", Example = """{ "secret": "ANTHROPIC_API_KEY" }""")]
    public SecretReference? ApiKey { get; init; }

    [Setting("Prices per million tokens, by model id, for reporting cost and enforcing cost budgets. Every model a profile uses needs one; the `claude` provider ships the prices of current models, and a configured price replaces a shipped one.",
        Example = """{ "claude-opus-5-5": { "input": 4, "output": 20, "cacheRead": 0.2, "cacheWrite5m": 5, "cacheWrite1h": 8 } }""")]
    public IReadOnlyDictionary<string, ModelPrice> Prices { get; init; } = new Dictionary<string, ModelPrice>();
}
