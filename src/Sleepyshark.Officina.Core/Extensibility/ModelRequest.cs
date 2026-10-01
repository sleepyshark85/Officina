using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// One model call, laid out as DESIGN.md §3 describes (CTX-01): the stable prefix (the tools, then the instructions),
/// then the history, which ends with the volatile context.
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
    /// Whether this request starts with exactly the content of <paramref name="previous"/>: the same prefix, and its
    /// history followed only by new messages (CTX-10). Cache boundaries may move.
    /// </summary>
    public bool StartsWith(ModelRequest previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return Profile == previous.Profile
            && Tools.SequenceEqual(previous.Tools)
            && Instructions == previous.Instructions
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

    /// <summary>The last block of history the provider can cache. A turn-scoped message is not one.</summary>
    History,
}
