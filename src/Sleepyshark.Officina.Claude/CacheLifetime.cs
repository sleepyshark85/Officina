namespace Sleepyshark.Officina.Claude;

/// <summary>How long Claude keeps a cache entry: five minutes, or an hour when reads come further apart.</summary>
public enum CacheLifetime
{
    FiveMinutes,
    OneHour,
}
