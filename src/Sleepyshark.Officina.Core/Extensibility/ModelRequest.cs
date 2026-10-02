using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// One model call, laid out as DESIGN.md §3 describes (CTX-01): the stable prefix (the tools, the instructions, then
/// project memory), then the history, which ends with the volatile context.
/// </summary>
/// <param name="Profile">The model profile.</param>
/// <param name="Tools">The tools the agent is offered, sorted by name (TOOL-03).</param>
/// <param name="Instructions">The agent's instructions, followed by how content is labelled (INV-08).</param>
/// <param name="History">
/// The conversation so far. Its last message is the volatile context: a turn-scoped system message where the provider
/// supports them, otherwise a user message with what changed since it was last sent (CTX-10).
/// </param>
/// <param name="CacheBoundaries">Where the provider caches the input and for how long, longest first (CTX-11).</param>
public sealed record ModelRequest(
    ModelProfile Profile,
    ImmutableArray<ToolDefinition> Tools,
    string Instructions,
    ImmutableArray<Message> History,
    ImmutableArray<CacheBoundary> CacheBoundaries)
{
    /// <summary>
    /// Whether the work needs no immediate answer, as in a batch, so the provider may use its lower-cost batch processing
    /// when it offers one (MDL-10).
    /// </summary>
    public bool Batch { get; init; }

    /// <summary>
    /// Project memory as it was when the conversation started, empty when there is none (MEM-01). It never changes within a
    /// conversation: later changes join the history as operator messages (MEM-03).
    /// </summary>
    public string Memory { get; init; } = "";

    /// <summary>
    /// The JSON Schema the agent's final output must match, when its output is structured (OUT-02), so a provider that
    /// constrains output natively can (CLD-06). The core checks the output against it either way.
    /// </summary>
    public System.Text.Json.JsonElement? OutputSchema { get; init; }

    /// <summary>
    /// Where the current turn starts in <see cref="History"/>: the messages before it are earlier turns, which a shortener may
    /// replace; the current turn it keeps unchanged (HIST-02).
    /// </summary>
    public int TurnStart { get; init; }

    /// <summary>
    /// Whether the request asks the model to summarize <see cref="History"/> instead of replying (HIST-01), when the model
    /// <see cref="ProviderCapabilities.Summarizes"/>. The reply's content is the summary, which takes the history's place.
    /// </summary>
    public bool Summarize { get; init; }

    /// <summary>
    /// Whether this request starts with exactly the content of <paramref name="previous"/>: the same prefix, and its
    /// history followed only by new messages (CTX-10). Cache boundaries may move.
    /// </summary>
    public bool StartsWith(ModelRequest previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return Profile == previous.Profile
            && Tools.SequenceEqual(previous.Tools)
            && Instructions == previous.Instructions
            && Memory == previous.Memory
            && History.Length >= previous.History.Length
            && History.Take(previous.History.Length).SequenceEqual(previous.History);
    }
}

/// <summary>A point up to which the provider caches the input (CTX-11).</summary>
/// <param name="After">The part of the input the boundary ends.</param>
/// <param name="Lifetime">How long the provider keeps the cache.</param>
public sealed record CacheBoundary(CachePoint After, TimeSpan Lifetime);

public enum CachePoint
{
    /// <summary>The end of the instructions: the part every agent of the definition on the model slot shares.</summary>
    Instructions,

    /// <summary>The end of project memory: the part shared by every agent of the definition on the model slot and memory scope.</summary>
    Memory,

    /// <summary>The last block of history the provider can cache. A turn-scoped message is not one.</summary>
    History,
}
