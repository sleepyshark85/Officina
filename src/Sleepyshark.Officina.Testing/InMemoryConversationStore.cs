using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>Keeps conversations in memory, for tests.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    internal TenantRows<ConversationTurn> Rows { get; } = new();

    /// <summary>Every turn, in every tenant, in the order appended.</summary>
    public IReadOnlyList<ConversationTurn> Turns => Rows.All;

    public ValueTask AppendAsync(string? tenant, ConversationTurn turn, CancellationToken ct)
    {
        Rows.Add(tenant, turn);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ConversationTurn>> ReadAsync(string? tenant, string agent, string? owner, CancellationToken ct)
    {
        var turns = Rows.Where(tenant, turn => turn.Agent == agent && turn.Owner == owner);
        var start = Math.Max(0, turns.ToList().FindLastIndex(turn => turn.Shortened));
        return ValueTask.FromResult<IReadOnlyList<ConversationTurn>>([.. turns.Skip(start)]);
    }
}
