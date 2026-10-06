namespace Sleepyshark.Officina;

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
