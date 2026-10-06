namespace Sleepyshark.Officina.Claude;

/// <summary>How long Claude keeps the cached prefix: five minutes, or an hour for users who reply slowly.</summary>
public enum CacheLifetime
{
    FiveMinutes,
    OneHour,
}
