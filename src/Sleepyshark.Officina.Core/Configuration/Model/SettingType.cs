using System.Collections.Immutable;

namespace Sleepyshark.Officina.Core.Configuration.Model;

public enum SettingKind
{
    /// <summary>A section with fixed settings, from an Options record.</summary>
    Section,

    /// <summary>Named items, such as agents (<see cref="NamedMap{T}"/>).</summary>
    Map,

    /// <summary>A list, replaced as a whole when layers merge (<see cref="ValueList{T}"/>).</summary>
    List,

    Text,
    WholeNumber,
    Number,
    Boolean,

    /// <summary>One of fixed values, from an enum.</summary>
    Choice,

    /// <summary>A <see cref="TimeSpan"/>, written as a number with a unit.</summary>
    Duration,

    /// <summary>A <see cref="SecretReference"/>, written <c>{ "secret": "NAME" }</c>.</summary>
    Secret,

    /// <summary>A <see cref="ModelReference"/>: a profile name or an inline profile.</summary>
    ModelReference,

    /// <summary>The capabilities section: one entry per registered capability, each with its own settings.</summary>
    Capabilities,

    /// <summary>Any JSON value (<see cref="SettingValue"/>).</summary>
    Any,

    /// <summary>A condition in the condition language (CFG-13).</summary>
    Condition,
}

/// <summary>The type of a setting's value, as the schema, the reference, the binder and validation see it.</summary>
public sealed class SettingType
{
    internal SettingType(SettingKind kind, Type clrType, ObjectShape? shape = null, SettingType? element = null, ImmutableArray<string> choices = default)
    {
        Kind = kind;
        ClrType = clrType;
        Shape = shape;
        Element = element;
        Choices = choices.IsDefault ? [] : choices;
    }

    public SettingKind Kind { get; }

    /// <summary>The CLR type of a value, never <see cref="Nullable{T}"/>.</summary>
    public Type ClrType { get; }

    /// <summary>The settings of an object, or of an inline model profile.</summary>
    public ObjectShape? Shape { get; }

    /// <summary>The type of a list's items or a map's values.</summary>
    public SettingType? Element { get; }

    /// <summary>The allowed values of a choice, as written in files.</summary>
    public ImmutableArray<string> Choices { get; }

    /// <summary>The value of an enum member as written in files.</summary>
    public string ChoiceName(object value) => Choices[Array.IndexOf(Enum.GetValues(ClrType), value)];

    /// <summary>The enum member a file's value names, or null.</summary>
    public object? ChoiceValue(string name, bool ignoreCase = false)
    {
        var index = Choices.IndexOf(name, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return index < 0 ? null : Enum.GetValues(ClrType).GetValue(index);
    }
}
