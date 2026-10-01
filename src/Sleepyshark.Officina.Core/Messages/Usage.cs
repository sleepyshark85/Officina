namespace Sleepyshark.Officina.Core.Messages;

/// <summary>Tokens used, counted separately as the provider bills them (MSG-06).</summary>
public sealed record Usage
{
    public Usage(long input, long output, long cacheRead, long cacheWrite)
    {
        Input = input;
        Output = output;
        CacheRead = cacheRead;
        CacheWrite = cacheWrite;
    }

    public static Usage None { get; } = new(0, 0, 0, 0);

    /// <summary>Input tokens that were neither read from nor written to the cache.</summary>
    public long Input { get; }

    public long Output { get; }

    public long CacheRead { get; }

    public long CacheWrite { get; }

    public long Total => Input + Output + CacheRead + CacheWrite;

    public static Usage operator +(Usage left, Usage right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return new(left.Input + right.Input, left.Output + right.Output, left.CacheRead + right.CacheRead, left.CacheWrite + right.CacheWrite);
    }
}
