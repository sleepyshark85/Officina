namespace BookshopAssistant;

/// <summary>The console's cost limits, in US dollars: per reply, and per session over all its replies.</summary>
public sealed record Budgets(decimal Reply, decimal Session)
{
    public static Budgets Default { get; } = new(Reply: 0.50m, Session: 5m);
}
