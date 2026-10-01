using System.Globalization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The rules about single values that <see cref="SettingAttribute"/> declares: required settings, ranges, and settings
/// that protect an invariant, whose removal or a value outside their range is an attempt to weaken it (phase 10).
/// Also: names of named entries cannot contain <c>.</c> or <c>[</c>, which setting paths use.
/// </summary>
internal static class SettingRules
{
    public static IEnumerable<ConfigurationError> Check(OfficinaOptions options) =>
        SettingWalker.Walk(options).SelectMany(Check);

    private static IEnumerable<ConfigurationError> Check(SettingVisit visit)
    {
        if (visit.EntryName is { } name && name.IndexOfAny(['.', '[', ']']) >= 0)
        {
            yield return new(ValidationPhase.Shape, visit.Path, $"\"{name}\" is not a valid name.", "Leave out '.', '[' and ']'.");
        }

        if (visit.Setting is not { } setting)
        {
            yield break;
        }

        if (setting.Required && (visit.Value is null || (visit.Value is string text && string.IsNullOrWhiteSpace(text))))
        {
            yield return new(ValidationPhase.Shape, visit.Path, "is required but not set.", "Add it; it has no default.");
        }
        else if (visit.Value is null && setting.Invariant is not null)
        {
            yield return new(ValidationPhase.Invariants, visit.Path, "cannot be removed.", "Set a value; it may be high, but it always exists.");
        }
        else if (BelowMinimum(setting, visit.Value) is { } limit)
        {
            yield return new(setting.Invariant is null ? ValidationPhase.Shape : ValidationPhase.Invariants, visit.Path,
                $"is {Describe(visit.Value!)}, but must be {limit}.",
                setting.Invariant is null ? $"Use a value {limit}." : "A limit can be high, but never zero, negative or unlimited.");
        }
    }

    private static string? BelowMinimum(SettingAttribute setting, object? value)
    {
        double? number = value switch
        {
            int whole => whole,
            decimal amount => (double)amount,
            TimeSpan duration => duration.TotalSeconds,
            _ => null,
        };
        var inRange = number is not { } given || double.IsNaN(setting.Minimum)
            || (setting.ExclusiveMinimum ? given > setting.Minimum : given >= setting.Minimum);
        if (inRange)
        {
            return null;
        }

        var minimum = value is TimeSpan
            ? Duration.Format(TimeSpan.FromSeconds(setting.Minimum))
            : setting.Minimum.ToString(CultureInfo.InvariantCulture);
        return (setting.ExclusiveMinimum ? "greater than " : "at least ") + minimum;
    }

    private static string Describe(object value) => value is TimeSpan duration
        ? Duration.Format(duration)
        : ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
}
