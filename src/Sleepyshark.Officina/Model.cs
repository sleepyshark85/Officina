using System.Collections.Immutable;

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

    /// <summary>
    /// Sends one request and streams the reply: text deltas and complete blocks as they arrive, usage, and last a
    /// <see cref="ModelStopped"/>. A retry after the reply has started is announced by a <see cref="ModelRestarted"/>.
    /// </summary>
    IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken);
}

/// <summary>One request in the fixed layout of CTX-01: tools sorted by name, frozen instructions, then the conversation.</summary>
public sealed record ModelRequest(ImmutableArray<Tool> Tools, string Instructions, ImmutableArray<Message> Messages);

/// <summary>Something the model streams while it replies.</summary>
public abstract record ModelEvent;

/// <summary>A piece of the reply's text, for display as it streams. The complete block follows in a <see cref="BlockReceived"/>.</summary>
public sealed record TextDelta(string Text) : ModelEvent;

/// <summary>A complete content block of the reply, in reply order.</summary>
public sealed record BlockReceived(ContentBlock Block) : ModelEvent;

/// <summary>
/// The call was retried after its reply had started (MDL-04): everything streamed before this belongs to a reply that
/// will not come, and the reply starts again.
/// </summary>
public sealed record ModelRestarted : ModelEvent;

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
public readonly record struct Usage(long Input, long Output, long CacheRead, long CacheWrite)
{
    public static Usage operator +(Usage left, Usage right) =>
        new(left.Input + right.Input, left.Output + right.Output, left.CacheRead + right.CacheRead, left.CacheWrite + right.CacheWrite);
}
