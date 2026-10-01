namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// Documents a setting of an Options class. The JSON Schema and the settings reference are generated
/// from these attributes and the property's type and initial value (CFG-15, CFG-16, DOC-01).
/// </summary>
/// <param name="description">What the setting means, in one or two sentences.</param>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string description) : Attribute
{
    public string Description { get; } = description;

    /// <summary>An example value, as JSON.</summary>
    public string? Example { get; init; }

    /// <summary>The smallest allowed value of a number or duration. <see cref="double.NaN"/> when there is none.</summary>
    public double Minimum { get; init; } = double.NaN;

    /// <summary>Whether <see cref="Minimum"/> itself is excluded.</summary>
    public bool ExclusiveMinimum { get; init; }

    /// <summary>The largest allowed value of a number. <see cref="double.NaN"/> when there is none.</summary>
    public double Maximum { get; init; } = double.NaN;

    /// <summary>
    /// The invariant this setting protects, such as <c>INV-07</c>. Such a setting cannot be removed with
    /// <c>null</c>, and a value outside its range is reported as an attempt to weaken the invariant.
    /// </summary>
    public string? Invariant { get; init; }

    /// <summary>Whether the owner may change the setting during a run (CFG-08).</summary>
    public bool Live { get; init; }

    /// <summary>Whether a file may give the text instead: <c>{ "file": "path" }</c>.</summary>
    public bool AllowFile { get; init; }

    /// <summary>Whether a setting with no default must be given. Non-nullable <c>required</c> properties are required anyway.</summary>
    public bool Required { get; init; }
}

/// <summary>A key that exists only in configuration files and is resolved while loading, such as <c>extends</c>.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class FileOnlySettingAttribute(string name, FileOnlySettingKind kind, string description) : Attribute
{
    public string Name { get; } = name;

    public FileOnlySettingKind Kind { get; } = kind;

    public string Description { get; } = description;

    /// <summary>An example value, as JSON.</summary>
    public string? Example { get; init; }
}

public enum FileOnlySettingKind
{
    /// <summary>A string.</summary>
    Text,

    /// <summary>A list of strings.</summary>
    TextList,
}
