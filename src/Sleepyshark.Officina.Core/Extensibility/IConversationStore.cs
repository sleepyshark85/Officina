using System.Collections.Immutable;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// Where each agent's conversation with each caller is kept, one turn at a time, so it continues across requests and
/// restarts (CAP-05). It is append-only: a shortened history is appended as a turn that replaces the ones before it
/// (HIST-01). Turns are visible only within their tenant (SEC-02).
/// </summary>
public interface IConversationStore
{
    ValueTask AppendAsync(string? tenant, ConversationTurn turn, CancellationToken ct);

    /// <summary>The conversation's turns in order, from its latest shortened turn.</summary>
    ValueTask<IReadOnlyList<ConversationTurn>> ReadAsync(string? tenant, string agent, string? owner, CancellationToken ct);

    /// <summary>How many turns the conversation has stored, from its first, which a checkpoint records (RUN-03).</summary>
    ValueTask<int> CountAsync(string? tenant, string agent, string? owner, CancellationToken ct);

    /// <summary>Deletes the turns after the first <paramref name="count"/>, when a run goes back to a checkpoint (RUN-08).</summary>
    ValueTask TruncateAsync(string? tenant, string agent, string? owner, int count, CancellationToken ct);
}

/// <summary>One turn of a conversation, as it was sent to the model.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Owner">The caller the conversation is with, or null for anonymous callers, who share one conversation per agent and tenant.</param>
/// <param name="Time">When the turn ended.</param>
/// <param name="Messages">The turn's messages, in which every tool request has its result.</param>
/// <param name="Shortened">Whether the history was shortened in this turn, so it holds the whole conversation.</param>
/// <param name="PrefixMemory">The memory revision in the conversation's prefix, which is the same in every turn of the conversation (MEM-03).</param>
/// <param name="SeenMemory">The memory revision the conversation has been told of: its prefix, and the changes appended to the history since.</param>
public sealed record ConversationTurn(
    string Agent, string? Owner, DateTimeOffset Time, ImmutableArray<Message> Messages, bool Shortened, long PrefixMemory = 0, long SeenMemory = 0);
