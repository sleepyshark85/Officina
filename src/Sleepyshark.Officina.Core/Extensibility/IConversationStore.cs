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
}

/// <summary>One turn of a conversation, as it was sent to the model.</summary>
/// <param name="Agent">The agent.</param>
/// <param name="Owner">The caller the conversation is with, or null for anonymous callers, who share one conversation per agent and tenant.</param>
/// <param name="Time">When the turn ended.</param>
/// <param name="Messages">The turn's messages, in which every tool request has its result.</param>
/// <param name="Shortened">Whether the history was shortened in this turn, so it holds the whole conversation.</param>
public sealed record ConversationTurn(string Agent, string? Owner, DateTimeOffset Time, ImmutableArray<Message> Messages, bool Shortened);
