namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Documents a setting of an Options class. The JSON Schema and the settings reference are generated from it, together
/// with the standard <c>[Required]</c> and <c>[Range]</c> annotations that validation checks (CFG-15, DOC-01).
/// </summary>
/// <param name="description">What the setting means, for users.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    /// <summary>An example value, as JSON.</summary>
    public required string Example { get; init; }

    /// <summary>Whether the owner may change the setting during a run (CFG-08).</summary>
    public bool Live { get; init; }
}
