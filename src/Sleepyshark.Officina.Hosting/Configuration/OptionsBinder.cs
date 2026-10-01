using System.Collections;
using System.Text.Json;
using Sleepyshark.Officina.Core.Conditions;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Creates the Options objects from the merged layers. The layers were checked before merging, so a value
/// that still does not fit is skipped and its setting keeps its default; the checker has reported it.
/// </summary>
internal sealed class OptionsBinder(SettingsModel model)
{
    public OfficinaOptions Bind(ConfigObject root) => (OfficinaOptions)BindSection(root, model.Root);

    private object BindSection(ConfigObject node, ObjectShape shape)
    {
        var instance = shape.CreateDefault();
        foreach (var property in shape.Properties)
        {
            if (node.Get(property.Name) is { } value and not ConfigNull && Read(value, property.Type) is { } bound)
            {
                property.SetValue(instance, bound);
            }
        }

        return instance;
    }

    private object? Read(ConfigNode node, SettingType type)
    {
        switch (type.Kind)
        {
            case SettingKind.Section when node is ConfigObject section:
                return BindSection(section, type.Shape!);
            case SettingKind.Map when node is ConfigObject map:
                return CreateMap(type, map.Properties.Select(entry => (entry.Key, Read(entry.Value, type.Element!))));
            case SettingKind.Capabilities when node is ConfigObject capabilities:
                return new NamedMap<CapabilitySettings>(capabilities.Properties
                    .Where(entry => entry.Value is ConfigObject && model.CapabilityShape(entry.Key) is not null)
                    .Select(entry => KeyValuePair.Create(entry.Key, (CapabilitySettings)BindSection((ConfigObject)entry.Value, model.CapabilityShape(entry.Key)!))));
            case SettingKind.List when node is ConfigArray list:
                var items = list.Items.Select(item => Read(item, type.Element!)).ToArray();
                return items.Any(item => item is null) ? null : CreateList(type, items!);
            case SettingKind.ModelReference when node is ConfigObject inline:
                return ModelReference.Inline((ModelProfile)BindSection(inline, type.Shape!));
            case SettingKind.ModelReference when node is ConfigScalar { Kind: JsonValueKind.String } name:
                return ModelReference.Named(name.Raw);
            case SettingKind.Secret when node is ConfigObject secret && secret.Get("secret") is ConfigScalar name:
                return new SecretReference(name.Raw);
            case SettingKind.Any:
                return SettingValue.From(node.ToElement());
            case SettingKind.Condition:
                return ConditionParser.Parse(node.ToElement(), "", []);
            case SettingKind.Text when node is ConfigScalar { Kind: JsonValueKind.String } text:
                return text.Raw;
            case SettingKind.WholeNumber when node is ConfigScalar { Kind: JsonValueKind.Number } number && number.TryGetDecimal(out var whole):
                return type.ClrType == typeof(int) ? (object)(int)whole : (long)whole;
            case SettingKind.Number when node is ConfigScalar { Kind: JsonValueKind.Number } number && number.TryGetDecimal(out var value):
                return type.ClrType == typeof(decimal) ? value : (double)value;
            case SettingKind.Boolean when node is ConfigScalar { Kind: JsonValueKind.True or JsonValueKind.False } flag:
                return flag.Kind == JsonValueKind.True;
            case SettingKind.Choice when node is ConfigScalar { Kind: JsonValueKind.String } choice:
                return type.ChoiceValue(choice.Raw);
            case SettingKind.Duration when node is ConfigScalar { Kind: JsonValueKind.String } text && Durations.TryParse(text.Raw, out var duration):
                return duration;
            default:
                return null;
        }
    }

    private static object CreateList(SettingType type, object[] items)
    {
        var typed = Array.CreateInstance(type.Element!.ClrType, items.Length);
        Array.Copy(items, typed, items.Length);
        return Activator.CreateInstance(type.ClrType, typed)!;
    }

    private static object CreateMap(SettingType type, IEnumerable<(string Name, object? Value)> entries)
    {
        var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), type.Element!.ClrType);
        var pairs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pairType))!;
        foreach (var (name, value) in entries.Where(entry => entry.Value is not null))
        {
            pairs.Add(Activator.CreateInstance(pairType, name, value));
        }

        return Activator.CreateInstance(type.ClrType, pairs)!;
    }
}
