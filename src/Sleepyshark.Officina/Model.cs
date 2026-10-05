using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>
/// A provider's model with its settings fixed (MDL-01, MDL-03): the only way the core reaches a model. Transient failures
/// are retried inside the implementation; a failure that remains is thrown, and the run ends as failed.
/// </summary>
public interface IModel
{
    /// <summary>
    /// The model and every setting that shapes its requests, as text that changes whenever any of them does. It is part of
    /// the prefix fingerprint (CTX-04).
    /// </summary>
    string Settings { get; }

    /// <summary>The provider, as telemetry names it (<c>gen_ai.provider.name</c>), such as <c>anthropic</c>.</summary>
    string Provider { get; }

    /// <summary>The model's identifier, as telemetry names it (<c>gen_ai.request.model</c>).</summary>
    string Name { get; }

    /// <summary>What the model's tokens cost (BUD-02); null when unknown, and then they cost nothing in results and budgets.</summary>
    ModelPrice? Price { get; }

    /// <summary>
    /// Sends one request and streams the reply: text deltas and complete blocks as they arrive, usage, and last a
    /// <see cref="ModelStopped"/>. Each retry is announced by a <see cref="ModelRetried"/>.
    /// </summary>
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>One request in the fixed layout of CTX-01: tools sorted by name, frozen instructions, then the conversation.</summary>
/// <param name="Tools">The tools, sorted by name.</param>
/// <param name="Instructions">The frozen instructions.</param>
/// <param name="Messages">The conversation, with the run's pending messages.</param>
/// <param name="MaxOutputTokens">
/// The most output tokens the remaining budget allows (BUD-01), when it limits them: the model uses this or its own
/// limit, whichever is lower. It is not part of the prefix.
/// </param>
public sealed record ModelRequest(ImmutableArray<Tool> Tools, string Instructions, ImmutableArray<Message> Messages, int? MaxOutputTokens = null);

/// <summary>Something the model streams while it replies.</summary>
public abstract record ModelEvent;

/// <summary>A piece of the reply's text, for display as it streams. The complete block follows in a <see cref="BlockReceived"/>.</summary>
public sealed record TextDelta(string Text) : ModelEvent;

/// <summary>A complete content block of the reply, in reply order.</summary>
public sealed record BlockReceived(ContentBlock Block) : ModelEvent;

/// <summary>
/// The call failed and is made again (MDL-04), whether or not its reply had started: everything streamed before this
/// belongs to a reply that will not come, and the reply starts again. Usage the failed attempt reported stays counted.
/// </summary>
public sealed record ModelRetried : ModelEvent;

/// <summary>Tokens the call used since its previous report: reports are increments, and the run adds them up.</summary>
public sealed record UsageReceived(Usage Usage) : ModelEvent;

/// <summary>Why the model stopped: the reply's last event.</summary>
/// <param name="Reason">The neutral reason.</param>
/// <param name="Detail">The refusal's category, or the provider's own word for an unknown reason.</param>
public sealed record ModelStopped(ModelStopReason Reason, string? Detail = null) : ModelEvent;

/// <summary>The model contract's stop reasons (ARCHITECTURE §4.1).</summary>
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
/// <param name="CacheWriteHour">Of <paramref name="CacheWrite"/>, the tokens written to the cache for an hour rather than the short default, which cost more.</param>
public readonly record struct Usage(long Input, long Output, long CacheRead, long CacheWrite, long CacheWriteHour = 0)
{
    /// <summary>All the tokens, of every kind.</summary>
    [JsonIgnore]
    public long Total => Input + Output + CacheRead + CacheWrite;

    public static Usage operator +(Usage left, Usage right) => new(
        left.Input + right.Input, left.Output + right.Output, left.CacheRead + right.CacheRead, left.CacheWrite + right.CacheWrite,
        left.CacheWriteHour + right.CacheWriteHour);
}

/// <summary>
/// A model's prices, in US dollars per million tokens (BUD-02). Cache writes are priced by how long the cache keeps them.
/// </summary>
/// <param name="Input">Input tokens neither read from nor written to the cache.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Input tokens read from the cache.</param>
/// <param name="CacheWrite">Input tokens written to the cache for the short default time.</param>
/// <param name="CacheWriteHour">Input tokens written to the cache for an hour.</param>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite, decimal CacheWriteHour)
{
    /// <summary>What <paramref name="usage"/> costs, in US dollars.</summary>
    public decimal Cost(Usage usage) =>
        ((usage.Input * Input) + (usage.Output * Output) + (usage.CacheRead * CacheRead)
            + ((usage.CacheWrite - usage.CacheWriteHour) * CacheWrite) + (usage.CacheWriteHour * CacheWriteHour)) / 1_000_000m;
}
