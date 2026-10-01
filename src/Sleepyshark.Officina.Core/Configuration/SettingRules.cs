using System.Globalization;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The rules <see cref="SettingAttribute"/> declares: ranges, and settings that protect an invariant, whose removal
/// or a value outside their range is an attempt to weaken it (phase 10). Also: every agent has instructions (CFG-17).
/// </summary>
internal static class SettingRules
{
    public static IEnumerable<ConfigurationError> Check(IEnumerable<SettingVisit> visits)
    {
        foreach (var visit in visits)
        {
            if (visit.Setting is not { } setting)
            {
                continue;
            }

            if (visit.Value is null && setting.Invariant is { } protects)
            {
                yield return new(ValidationPhase.Invariants, visit.Path, "cannot be removed.", $"Set a value; it may be high, but it always exists ({protects}).");
            }
            else if (visit.Path.EndsWith(".instructions", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(visit.Value as string))
            {
                yield return new(ValidationPhase.Shape, visit.Path, "is required but not set.",
                    $"Add \"instructions\" to {visit.Path[..^".instructions".Length]}: the agent's job. Only the application knows it (CFG-17).");
            }
            else if (BelowMinimum(setting, visit.Value) is { } limit)
            {
                yield return new(setting.Invariant is null ? ValidationPhase.Shape : ValidationPhase.Invariants, visit.Path,
                    $"is {Describe(visit.Value!)}, but must be {limit}.",
                    setting.Invariant is { } invariant ? $"A limit can be high, but never zero, negative or unlimited ({invariant})." : $"Use a value {limit}.");
            }
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
        if (number is not { } given || double.IsNaN(setting.Minimum) || (setting.ExclusiveMinimum ? given > setting.Minimum : given >= setting.Minimum))
        {
            return null;
        }

        var minimum = value is TimeSpan ? OfficinaJson.FormatDuration(TimeSpan.FromSeconds(setting.Minimum)) : setting.Minimum.ToString(CultureInfo.InvariantCulture);
        return (setting.ExclusiveMinimum ? "greater than " : "at least ") + minimum;
    }

    private static string Describe(object value) => value is TimeSpan duration
        ? OfficinaJson.FormatDuration(duration)
        : ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
}
