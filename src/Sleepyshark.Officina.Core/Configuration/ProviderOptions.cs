namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A model provider. Later slices add retries, timeouts and features.</summary>
public sealed record ProviderOptions
{
    /// <summary>The name of the provider the core configures by default.</summary>
    public const string ClaudeName = "claude";

    /// <summary>
    /// The default <c>claude</c> provider, which reads the secret <c>ANTHROPIC_API_KEY</c>. A new one each time: the
    /// configuration binder adds configured prices into the default's dictionary, which must not carry over to another load.
    /// </summary>
    public static ProviderOptions Claude => new() { ApiKey = new SecretReference("ANTHROPIC_API_KEY") };

    [Setting("The provider's credential, as the name of a secret, which is read when it is used.", Example = """{ "secret": "ANTHROPIC_API_KEY" }""")]
    public SecretReference? ApiKey { get; init; }

    [Setting("Prices per million tokens, by model id, for reporting cost and enforcing cost budgets. A model without a price costs nothing.",
        Example = """{ "claude-opus-5-5": { "input": 4, "output": 20, "cacheRead": 0.2, "cacheWrite": 5 } }""")]
    public IReadOnlyDictionary<string, ModelPrice> Prices { get; init; } = new Dictionary<string, ModelPrice>();
}
