using System.Collections.Immutable;
using System.Reflection;

namespace Sleepyshark.Officina.Core.Configuration.Model;

/// <summary>The settings of one Options record, in declaration order.</summary>
public sealed class ObjectShape
{
    private ImmutableArray<SettingProperty> properties = [];

    internal ObjectShape(Type type)
    {
        Type = type;
        FileOnly = [.. type.GetCustomAttributes<FileOnlySettingAttribute>()];
    }

    public Type Type { get; }

    public ImmutableArray<SettingProperty> Properties => properties;

    /// <summary>Keys that only files have, such as <c>extends</c>.</summary>
    public ImmutableArray<FileOnlySettingAttribute> FileOnly { get; }

    public SettingProperty? Find(string name, bool ignoreCase = false) =>
        properties.FirstOrDefault(property => string.Equals(property.Name, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    public FileOnlySettingAttribute? FindFileOnly(string name, bool ignoreCase = false) =>
        FileOnly.FirstOrDefault(key => string.Equals(key.Name, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

    /// <summary>Every key a file may use here.</summary>
    public IEnumerable<string> KeyNames => properties.Select(property => property.Name).Concat(FileOnly.Select(key => key.Name));

    /// <summary>A new instance with every setting at its default.</summary>
    public object CreateDefault() => Activator.CreateInstance(Type)!;

    internal void SetProperties(ImmutableArray<SettingProperty> value) => properties = value;
}

/// <summary>One setting of an Options record.</summary>
public sealed class SettingProperty
{
    internal SettingProperty(PropertyInfo property, string name, SettingType type, bool nullable, bool required, SettingAttribute info)
    {
        Property = property;
        Name = name;
        Type = type;
        Nullable = nullable;
        Required = required;
        Info = info;
    }

    public PropertyInfo Property { get; }

    /// <summary>The name used in files.</summary>
    public string Name { get; }

    public SettingType Type { get; }

    /// <summary>Whether the setting may be unset (null), which means "use the provider's or the core's behaviour".</summary>
    public bool Nullable { get; }

    /// <summary>Whether the setting has no default and must be given.</summary>
    public bool Required { get; }

    public SettingAttribute Info { get; }

    public object? GetValue(object owner) => Property.GetValue(owner);

    public void SetValue(object owner, object? value) => Property.SetValue(owner, value);
}
