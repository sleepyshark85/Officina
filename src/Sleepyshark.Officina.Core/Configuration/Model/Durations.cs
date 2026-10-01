using System.Globalization;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration.Model;

/// <summary>Durations as written in configuration: a number with a unit, <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c> or <c>d</c>, such as <c>"30m"</c>.</summary>
public static partial class Durations
{
    /// <summary>The pattern of a duration, also used in the JSON Schema.</summary>
    public const string Pattern = "^[0-9]+(\\.[0-9]+)?(ms|s|m|h|d)$";

    private static readonly (string Unit, TimeSpan Size)[] Units =
    [
        ("d", TimeSpan.FromDays(1)), ("h", TimeSpan.FromHours(1)), ("m", TimeSpan.FromMinutes(1)),
        ("s", TimeSpan.FromSeconds(1)), ("ms", TimeSpan.FromMilliseconds(1)),
    ];

    public static bool TryParse(string text, out TimeSpan duration)
    {
        duration = default;
        var match = Expression().Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var unit = Units.First(candidate => candidate.Unit == match.Groups["unit"].Value).Size;
        var ticks = number * unit.Ticks;
        if (ticks > TimeSpan.MaxValue.Ticks)
        {
            return false;
        }

        duration = TimeSpan.FromTicks((long)ticks);
        return true;
    }

    /// <summary>Writes a duration in the largest unit that represents it exactly, such as <c>"8h"</c> or <c>"90s"</c>.</summary>
    public static string Format(TimeSpan duration)
    {
        if (duration == TimeSpan.Zero)
        {
            return "0s";
        }

        foreach (var (unit, size) in Units)
        {
            if (duration.Ticks % size.Ticks == 0)
            {
                return (duration.Ticks / size.Ticks).ToString(CultureInfo.InvariantCulture) + unit;
            }
        }

        return (duration.Ticks / (decimal)TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) + "ms";
    }

    [GeneratedRegex("^(?<number>[0-9]+(\\.[0-9]+)?)(?<unit>ms|s|m|h|d)$")]
    private static partial Regex Expression();
}
