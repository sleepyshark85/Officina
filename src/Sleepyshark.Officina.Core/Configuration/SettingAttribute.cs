namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Declares a setting of an Options class: its documentation, from which the JSON Schema and the settings reference
/// are generated (CFG-15, DOC-01), and the rules about its single value, which validation checks (CFG-06).
/// </summary>
/// <param name="description">What the setting means, for users.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    /// <summary>An example value, as JSON.</summary>
    public required string Example { get; init; }

    /// <summary>Whether the setting must be given and must not be blank.</summary>
    public bool Required { get; init; }

    /// <summary>The smallest allowed value of a number or duration, or <see cref="double.NaN"/>.</summary>
    public double Minimum { get; init; } = double.NaN;

    /// <summary>Whether <see cref="Minimum"/> itself is excluded.</summary>
    public bool ExclusiveMinimum { get; init; }

    /// <summary>
    /// The invariant the setting protects, such as <c>INV-07</c>, for maintainers. Such a setting cannot be removed,
    /// and a value outside its range is reported as an attempt to weaken it.
    /// </summary>
    public string? Invariant { get; init; }

    /// <summary>Whether the owner may change the setting during a run (CFG-08).</summary>
    public bool Live { get; init; }
}
