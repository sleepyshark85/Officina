using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>A model provider. Later slices add features.</summary>
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
            ["claude-opus-4-8"] = new() { Input = 5m, Output = 25m, CacheRead = 0.5m, CacheWrite5m = 6.25m, CacheWrite1h = 10m },
            ["claude-sonnet-5-5"] = new() { Input = 2m, Output = 10m, CacheRead = 0.2m, CacheWrite5m = 2.5m, CacheWrite1h = 4m },
            ["claude-sonnet-5"] = new() { Input = 2m, Output = 10m, CacheRead = 0.2m, CacheWrite5m = 2.5m, CacheWrite1h = 4m },
            ["claude-haiku-4-5"] = new() { Input = 1m, Output = 5m, CacheRead = 0.1m, CacheWrite5m = 1.25m, CacheWrite1h = 2m },
        },
    };

    [Setting("The provider's credential, as the name of a secret, which is read when it is used.", Example = """{ "secret": "ANTHROPIC_API_KEY" }""")]
    public SecretReference? ApiKey { get; init; }

    [Setting("Prices per million tokens, by model id, for reporting cost and enforcing cost budgets. Every model a profile uses needs one; the `claude` provider ships the prices of current models, and a configured price overrides the shipped values it sets.",
        Example = """{ "claude-opus-5-5": { "input": 4, "output": 20, "cacheRead": 0.2, "cacheWrite5m": 5, "cacheWrite1h": 8 } }""")]
    public IReadOnlyDictionary<string, ModelPrice> Prices { get; init; } = new Dictionary<string, ModelPrice>();

    [Setting("How long to wait for the provider to answer a call, and then for each next piece of its streamed reply, as `hh:mm:ss`. A call that goes quiet for longer is given up on as a transient failure and retried.", Example = "\"00:10:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);

    [Setting("How failed calls are retried, for every agent that calls this provider.", Example = """{ "maxAttempts": 5, "initialDelay": "00:00:01" }""")]
    public RetryOptions Retry { get; init; } = new();

    [Setting("The most calls to this provider in flight at once, across all agents: the account's share of the provider's rate limit. Calls over it wait their turn, the team lead's first. Unset means no limit.",
        Example = "8")]
    [Range(1, int.MaxValue, ErrorMessage = "must be at least 1.")]
    public int? MaxConcurrentCalls { get; init; }

    [Setting("Features of the provider's own API, each off until switched on. The `claude` provider has them all; a provider without one ignores it.",
        Example = """{ "structuredOutput": true, "refusalFallback": true }""")]
    public ProviderFeatures Features { get; init; } = new();
}

/// <summary>Features of a provider's own API, switched on in configuration (CLD-06).</summary>
public sealed record ProviderFeatures
{
    [Setting("Whether the model's output is constrained to the agent's `output.schema` natively (Claude's `output_config.format`). The core checks the output against the schema either way. The provider may not accept every JSON Schema keyword.",
        Example = "true")]
    public bool StructuredOutput { get; init; }

    [Setting("Whether the provider clears the results of old tool calls from what the model reads once the conversation grows long (Claude's `clear_tool_uses` context editing). The stored history keeps them.",
        Example = "true")]
    public bool ClearToolResults { get; init; }

    [Setting("The tokens a turn may generate and read from tool results, told to the model so it paces its work (Claude's task budget, at least 20,000). It is advice to the model: the limits in `budget` are what stop a turn. Unset means none.",
        Example = "200000")]
    [Range(20_000, int.MaxValue, ErrorMessage = "must be at least 20000.")]
    public int? TaskBudget { get; init; }

    [Setting("Whether a call the model's safety classifiers decline is served by the fallback model the provider recommends for the refusal's category (Claude's server-side `fallbacks`, on the Claude API). Each model is priced as it serves, so every model it may use needs a price; a `modelFallback` event names it.",
        Example = "true")]
    public bool RefusalFallback { get; init; }

    /// <summary>The setting names of the features switched on.</summary>
    public IEnumerable<string> On()
    {
        if (StructuredOutput)
        {
            yield return "structuredOutput";
        }

        if (ClearToolResults)
        {
            yield return "clearToolResults";
        }

        if (TaskBudget is not null)
        {
            yield return "taskBudget";
        }

        if (RefusalFallback)
        {
            yield return "refusalFallback";
        }
    }
}
