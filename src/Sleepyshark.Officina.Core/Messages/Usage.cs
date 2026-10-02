namespace Sleepyshark.Officina.Core.Messages;

/// <summary>Tokens used, counted separately as the provider bills them (MSG-06).</summary>
public sealed record Usage
{
    /// <param name="input">Input tokens that were neither read from nor written to the cache.</param>
    /// <param name="output">Output tokens.</param>
    /// <param name="cacheRead">Input tokens read from the cache.</param>
    /// <param name="cacheWrite">Input tokens written to the cache.</param>
    /// <param name="cacheWrite1h">Of <paramref name="cacheWrite"/>, those kept for an hour, which cost more; the rest are kept for five minutes.</param>
    public Usage(long input, long output, long cacheRead, long cacheWrite, long cacheWrite1h = 0)
    {
        Input = input;
        Output = output;
        CacheRead = cacheRead;
        CacheWrite = cacheWrite;
        CacheWrite1h = cacheWrite1h;
    }

    public static Usage None { get; } = new(0, 0, 0, 0);

    /// <summary>Input tokens that were neither read from nor written to the cache.</summary>
    public long Input { get; }

    public long Output { get; }

    public long CacheRead { get; }

    public long CacheWrite { get; }

    /// <summary>Of <see cref="CacheWrite"/>, the tokens kept for an hour; the rest are kept for five minutes.</summary>
    public long CacheWrite1h { get; }

    public long Total => Input + Output + CacheRead + CacheWrite;

    public static Usage operator +(Usage left, Usage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(
            left.Input + right.Input, left.Output + right.Output, left.CacheRead + right.CacheRead, left.CacheWrite + right.CacheWrite,
            left.CacheWrite1h + right.CacheWrite1h);
    }
}
