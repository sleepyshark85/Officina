namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Claude's list prices, in US dollars per million tokens: cache reads at the model's rate, cache writes at 1.25 times
/// input for five minutes and twice input for an hour. A host with other prices sets <see cref="ClaudeModel.Price"/>.
/// </summary>
public static class ClaudePrices
{
    public static IReadOnlyDictionary<string, ModelPrice> Table { get; } = new Dictionary<string, ModelPrice>(StringComparer.Ordinal)
    {
        ["claude-opus-5-5"] = Price(input: 4m, output: 20m, cacheRead: 0.20m),
    };

    private static ModelPrice Price(decimal input, decimal output, decimal cacheRead) =>
        new(input, output, cacheRead, CacheWrite: input * 1.25m, CacheWriteHour: input * 2m);
}
