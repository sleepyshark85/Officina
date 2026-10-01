namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Validates bound Options and reports every error, ordered by phase (CFG-06). Rules are split by what they need to
/// see: single-value rules are declared with <see cref="SettingAttribute"/>, and rules across sections are central.
/// Files add the parse, shape and merge errors only they can have; the programmatic form gets the rest here (CFG-02).
/// </summary>
public static class ConfigurationValidator
{
    public static IReadOnlyList<ConfigurationError> Validate(OfficinaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return [.. SettingRules.Check(options)
            .Concat(ReferenceRules.Check(options))
            .Concat(InstructionPlaceholders.Check(options))
            .OrderBy(error => error.Phase)];
    }
}
