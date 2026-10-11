using System.Text.Json.Serialization;

namespace Sleepyshark.Officina;

/// <summary>Tokens, counted as the provider bills them.</summary>
/// <param name="Input">Input tokens neither read from nor written to the cache.</param>
/// <param name="Output">Output tokens.</param>
/// <param name="CacheRead">Input tokens read from the cache.</param>
/// <param name="CacheWrite">Input tokens written to the cache.</param>
/// <param name="CacheWriteHour">Of <paramref name="CacheWrite"/>, those cached for an hour, which cost more.</param>
public readonly record struct Usage(long Input, long Output, long CacheRead, long CacheWrite, long CacheWriteHour = 0)
{
    /// <summary>All the tokens, of every kind.</summary>
    [JsonIgnore]
    public long Total => Input + Output + CacheRead + CacheWrite;

    public static Usage operator +(Usage left, Usage right) => new(
        left.Input + right.Input, left.Output + right.Output, left.CacheRead + right.CacheRead, left.CacheWrite + right.CacheWrite,
        left.CacheWriteHour + right.CacheWriteHour);
}
