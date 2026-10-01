using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sleepyshark.Officina.Core.Capabilities;
using Sleepyshark.Officina.Core.Conditions;

namespace Sleepyshark.Officina.Core.Configuration.Model;

/// <summary>
/// What the Options classes declare, read by reflection: every setting, its type, default and documentation.
/// The schema, the settings reference, binding and validation all read this one model, so they cannot drift
/// apart (CFG-15, CFG-16, DOC-01).
/// </summary>
public sealed class SettingsModel
{
    private static readonly SettingsModel DefaultModel = new(CapabilityRegistry.Empty);

    private readonly Lock gate = new();
    private readonly Dictionary<Type, ObjectShape> shapes = [];
    private readonly NullabilityInfoContext nullability = new();

    private SettingsModel(CapabilityRegistry capabilities)
    {
        Capabilities = capabilities;
        Root = ShapeOf(typeof(OfficinaOptions));
        foreach (var capability in capabilities.All)
        {
            ShapeOf(capability.SettingsType);
        }
    }

    /// <summary>The model with no capabilities registered.</summary>
    public static SettingsModel Default => DefaultModel;

    public CapabilityRegistry Capabilities { get; }

    /// <summary>The top-level settings.</summary>
    public ObjectShape Root { get; }

    public static SettingsModel For(CapabilityRegistry capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.All.Count == 0 ? DefaultModel : new SettingsModel(capabilities);
    }

    /// <summary>The settings of a registered capability, or null when no capability has that name.</summary>
    public ObjectShape? CapabilityShape(string name) =>
        Capabilities.Find(name) is { } capability ? ShapeOf(capability.SettingsType) : null;

    public ObjectShape ShapeOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (gate)
        {
            if (shapes.TryGetValue(type, out var known))
            {
                return known;
            }

            var shape = new ObjectShape(type);
            shapes[type] = shape;
            shape.SetProperties([.. SettingsOf(type)]);
            return shape;
        }
    }

    private IEnumerable<SettingProperty> SettingsOf(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"Options class {type.Name} needs a parameterless constructor, so its initial values can be the defaults (CFG-16).");
        }

        // Base-class settings first, such as a capability's "enabled".
        var hierarchy = new Stack<Type>();
        for (var current = type; current is not null && current != typeof(object); current = current.BaseType)
        {
            hierarchy.Push(current);
        }

        foreach (var declaring in hierarchy)
        {
            foreach (var property in declaring.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (property.SetMethod is null || !property.SetMethod.IsPublic)
                {
                    continue;
                }

                var info = property.GetCustomAttribute<SettingAttribute>()
                    ?? throw new InvalidOperationException($"{declaring.Name}.{property.Name} has no [Setting] attribute. Every setting is documented (DOC-01).");

                var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null
                    || nullability.Create(property).WriteState == NullabilityState.Nullable;
                var required = property.GetCustomAttribute<RequiredMemberAttribute>() is not null || info.Required;
                var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                yield return new SettingProperty(property, name, TypeOf(property.PropertyType, declaring, property.Name), nullable, required, info);
            }
        }
    }

    private SettingType TypeOf(Type type, Type owner, string propertyName)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        if (type == typeof(string))
        {
            return new SettingType(SettingKind.Text, type);
        }

        if (type == typeof(int) || type == typeof(long))
        {
            return new SettingType(SettingKind.WholeNumber, type);
        }

        if (type == typeof(decimal) || type == typeof(double))
        {
            return new SettingType(SettingKind.Number, type);
        }

        if (type == typeof(bool))
        {
            return new SettingType(SettingKind.Boolean, type);
        }

        if (type.IsEnum)
        {
            return new SettingType(SettingKind.Choice, type, choices: [.. Enum.GetNames(type).Select(JsonNamingPolicy.CamelCase.ConvertName)]);
        }

        if (type == typeof(TimeSpan))
        {
            return new SettingType(SettingKind.Duration, type);
        }

        if (type == typeof(SecretReference))
        {
            return new SettingType(SettingKind.Secret, type);
        }

        if (type == typeof(ModelReference))
        {
            return new SettingType(SettingKind.ModelReference, type, ShapeOf(typeof(ModelProfile)));
        }

        if (type == typeof(SettingValue))
        {
            return new SettingType(SettingKind.Any, type);
        }

        if (type == typeof(Condition))
        {
            return new SettingType(SettingKind.Condition, type);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ValueList<>))
        {
            return new SettingType(SettingKind.List, type, element: TypeOf(type.GetGenericArguments()[0], owner, propertyName));
        }

        if (type == typeof(NamedMap<CapabilitySettings>))
        {
            return new SettingType(SettingKind.Capabilities, type);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NamedMap<>))
        {
            return new SettingType(SettingKind.Map, type, element: TypeOf(type.GetGenericArguments()[0], owner, propertyName));
        }

        if (type.IsClass && !type.IsAbstract && type.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
        {
            return new SettingType(SettingKind.Section, type, ShapeOf(type));
        }

        throw new InvalidOperationException($"{owner.Name}.{propertyName} has the type {type.Name}, which configuration cannot express.");
    }
}
