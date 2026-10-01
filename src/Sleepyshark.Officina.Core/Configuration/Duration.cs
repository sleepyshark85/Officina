using System.Globalization;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Durations as written in configuration: a number with a unit, <c>ms</c>, <c>s</c>, <c>m</c>, <c>h</c> or <c>d</c>,
/// such as <c>"30m"</c>.
/// </summary>
public static partial class Duration
{
    /// <summary>The pattern of a duration, also used in the JSON Schema. Seven digits keep every match within a <see cref="TimeSpan"/>.</summary>
    public const string Pattern = "^[0-9]{1,7}(\\.[0-9]+)?(ms|s|m|h|d)$";

    private static readonly (string Unit, TimeSpan Size)[] Units =
    [
        ("d", TimeSpan.FromDays(1)), ("h", TimeSpan.FromHours(1)), ("m", TimeSpan.FromMinutes(1)),
        ("s", TimeSpan.FromSeconds(1)), ("ms", TimeSpan.FromMilliseconds(1)),
    ];

    /// <summary>Reads a duration; false when the text is not one, or is longer than a <see cref="TimeSpan"/> can hold.</summary>
    public static bool TryParse(string text, out TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(text);
        duration = default;
        var match = Expression().Match(text);
        var digits = match.Groups["number"].Value;
        if (!match.Success || !decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        var unit = Units.First(candidate => candidate.Unit == match.Groups["unit"].Value).Size;
        if (number > TimeSpan.MaxValue.Ticks / unit.Ticks)
        {
            return false;
        }

        duration = TimeSpan.FromTicks((long)(number * unit.Ticks));
        return true;
    }

    /// <summary>Writes a duration in the largest unit that represents it exactly, such as <c>"8h"</c>.</summary>
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

        return duration.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
    }

    [GeneratedRegex("^(?<number>[0-9]{1,7}(\\.[0-9]+)?)(?<unit>ms|s|m|h|d)$")]
    private static partial Regex Expression();
}
