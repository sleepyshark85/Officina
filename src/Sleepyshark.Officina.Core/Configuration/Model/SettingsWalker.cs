using System.Collections;

namespace Sleepyshark.Officina.Core.Configuration.Model;

/// <summary>One setting reached by <see cref="SettingsWalker"/>.</summary>
/// <param name="Path">The setting's path.</param>
/// <param name="Property">The declaring property, or null for a list item or map entry.</param>
/// <param name="Type">The value's type.</param>
/// <param name="Value">The value, or null when unset.</param>
/// <param name="EntryName">The name of a map entry, or null.</param>
public sealed record SettingVisit(string Path, SettingProperty? Property, SettingType Type, object? Value, string? EntryName);

/// <summary>Visits every setting of bound Options, with its path, for rules that apply to settings of any section.</summary>
public static class SettingsWalker
{
    /// <summary>Visits every setting under the root. Capabilities that are off are skipped, since their settings are not in effect (CAP-02).</summary>
    public static IEnumerable<SettingVisit> Walk(OfficinaOptions options, SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(model);
        return WalkObject(options, model.Root, "", model);
    }

    private static IEnumerable<SettingVisit> WalkObject(object owner, ObjectShape shape, string path, SettingsModel model)
    {
        foreach (var property in shape.Properties)
        {
            var childPath = SettingPath.Child(path, property.Name);
            var value = property.GetValue(owner);
            yield return new SettingVisit(childPath, property, property.Type, value, null);
            if (value is not null)
            {
                foreach (var visit in WalkValue(value, property.Type, childPath, model))
                {
                    yield return visit;
                }
            }
        }
    }

    private static IEnumerable<SettingVisit> WalkValue(object value, SettingType type, string path, SettingsModel model)
    {
        switch (type.Kind)
        {
            case SettingKind.Section:
                return WalkObject(value, type.Shape!, path, model);
            case SettingKind.ModelReference when value is ModelReference { Profile: { } profile }:
                return WalkObject(profile, type.Shape!, path, model);
            case SettingKind.List:
                return ((IEnumerable)value).Cast<object>().SelectMany((item, index) =>
                    Prepend(new SettingVisit(SettingPath.Item(path, index), null, type.Element!, item, null), WalkValue(item, type.Element!, SettingPath.Item(path, index), model)));
            case SettingKind.Map:
                return Entries(value).SelectMany(entry =>
                {
                    var entryPath = SettingPath.Child(path, entry.Name);
                    return Prepend(new SettingVisit(entryPath, null, type.Element!, entry.Value, entry.Name), WalkValue(entry.Value, type.Element!, entryPath, model));
                });
            case SettingKind.Capabilities:
                // Only registered capabilities: an unknown one is reported as such, not by its settings.
                return ((NamedMap<CapabilitySettings>)value)
                    .Where(entry => entry.Value.Enabled && model.Capabilities.Find(entry.Key)?.SettingsType == entry.Value.GetType())
                    .SelectMany(entry => WalkObject(entry.Value, model.ShapeOf(entry.Value.GetType()), SettingPath.Child(path, entry.Key), model));
            default:
                return [];
        }
    }

    private static IEnumerable<SettingVisit> Prepend(SettingVisit first, IEnumerable<SettingVisit> rest) => rest.Prepend(first);

    private static IEnumerable<(string Name, object Value)> Entries(object map)
    {
        foreach (var entry in (IEnumerable)map)
        {
            var type = entry.GetType();
            yield return ((string)type.GetProperty("Key")!.GetValue(entry)!, type.GetProperty("Value")!.GetValue(entry)!);
        }
    }
}
