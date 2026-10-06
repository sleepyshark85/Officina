using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>
/// A provider's model with fixed settings: the only way the core reaches a model. It retries transient failures itself;
/// a failure that remains is thrown, and the run fails.
/// </summary>
public interface IModel
{
    /// <summary>
    /// The model and every setting that shapes its requests, as text that changes when any of them does: it is part of the
    /// prefix fingerprint.
    /// </summary>
    string Settings { get; }

    /// <summary>The provider, as telemetry names it (<c>gen_ai.provider.name</c>), such as <c>anthropic</c>.</summary>
    string Provider { get; }

    /// <summary>The model's identifier, as telemetry names it (<c>gen_ai.request.model</c>).</summary>
    string Name { get; }

    /// <summary>What the model's tokens cost; null when unknown, and then they cost nothing in results and budgets.</summary>
    ModelPrice? Price { get; }

    /// <summary>What the provider supports beyond the basic contract; none unless the model declares it.</summary>
    ModelCapabilities Capabilities => ModelCapabilities.None;

    /// <summary>
    /// Sends one request and streams the reply: text deltas and complete blocks as they arrive, usage, and last a
    /// <see cref="ModelStopped"/>. Each retry is announced by a <see cref="ModelRetried"/>.
    /// </summary>
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>One request: the prefix that stays the same for the conversation, then the conversation.</summary>
/// <param name="Prefix">Model settings, sorted tools, instructions, output schema and context management.</param>
/// <param name="Messages">The conversation, with the run's pending messages.</param>
/// <param name="MaxOutputTokens">
/// The most output tokens the remaining budget allows, if it limits them; the model uses the lower of this and its own.
/// Not part of the prefix.
/// </param>
public sealed record ModelRequest(RequestPrefix Prefix, ImmutableArray<Message> Messages, int? MaxOutputTokens = null);

/// <summary>Something the model streams while it replies.</summary>
public abstract record ModelEvent;

/// <summary>A piece of reply text, for display as it streams; the complete block follows in a <see cref="BlockReceived"/>.</summary>
public sealed record TextDelta(string Text) : ModelEvent;

/// <summary>A complete content block of the reply, in reply order.</summary>
public sealed record BlockReceived(ContentBlock Block) : ModelEvent;

/// <summary>
/// The call failed and is made again: everything streamed before this belongs to a reply that will not come. Usage the
/// failed attempt reported stays counted.
/// </summary>
public sealed record ModelRetried : ModelEvent;

/// <summary>Tokens used since the call's previous report: reports are increments.</summary>
public sealed record UsageReceived(Usage Usage) : ModelEvent;

/// <summary>Why the model stopped: the reply's last event.</summary>
/// <param name="Reason">The neutral reason.</param>
/// <param name="Detail">The refusal's category, or the provider's own word for an unknown reason.</param>
public sealed record ModelStopped(ModelStopReason Reason, string? Detail = null) : ModelEvent;

/// <summary>Why a model stopped, in the model contract's words.</summary>
public enum ModelStopReason
{
    /// <summary>A reason the provider gave that is none of the others.</summary>
    Unknown,
    End,
    ToolUse,
    MaxTokens,
    Refusal,
    ContextFull,
}

/// <summary>Tokens, counted as the provider bills them.</summary>
/// <param name="Input">Input tokens neither read from nor written to the cache.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Input tokens read from the cache.</param>
/// <param name="CacheWrite">Input tokens written to the cache.</param>
/// <param name="CacheWriteHour">Of <paramref name="CacheWrite"/>, those cached for an hour, which cost more.</param>
public readonly record struct Usage(long Input, long Output, long CacheRead, long CacheWrite, long CacheWriteHour = 0)
{
    /// <summary>All the tokens, of every kind.</summary>
    [JsonIgnore]
    public long Total => Input + Output + CacheRead + CacheWrite;

    public static Usage operator +(Usage left, Usage right) => new(
        left.Input + right.Input, left.Output + right.Output, left.CacheRead + right.CacheRead, left.CacheWrite + right.CacheWrite,
        left.CacheWriteHour + right.CacheWriteHour);
}

/// <summary>A model's prices, in US dollars per million tokens.</summary>
/// <param name="Input">Input tokens neither read from nor written to the cache.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Input tokens read from the cache.</param>
/// <param name="CacheWrite">Input tokens cached for the short default time.</param>
/// <param name="CacheWriteHour">Input tokens cached for an hour.</param>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, decimal CacheWriteHour)
{
    /// <summary>What <paramref name="usage"/> costs, in US dollars.</summary>
    public decimal Cost(Usage usage) =>
        ((usage.Input * Input) + (usage.Output * Output) + (usage.CacheRead * CacheRead)
            + ((usage.CacheWrite - usage.CacheWriteHour) * CacheWrite) + (usage.CacheWriteHour * CacheWriteHour)) / 1_000_000m;
}
