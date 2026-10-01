using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Conditions;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Checks one layer against the settings model before it is merged (phase 2): unknown settings, wrong
/// types, plain-text secrets and removing protected settings. It reports every problem with the layer's
/// own positions, and returns the layer in normal form: capability shorthand expanded, text from
/// environment variables read as the type the setting expects, and file includes read.
/// </summary>
internal sealed partial class LayerChecker(SettingsModel model, string baseDirectory, ICollection<ConfigurationError> errors, ISet<string> badPaths)
{
    private bool ignoreCase;

    public ConfigObject Check(ConfigObject root, bool ignoreCaseOfSettingNames)
    {
        ignoreCase = ignoreCaseOfSettingNames;
        return CheckSection(root, model.Root, "");
    }

    private ConfigObject CheckSection(ConfigObject node, ObjectShape shape, string path)
    {
        var properties = new List<KeyValuePair<string, ConfigNode>>();
        foreach (var (key, value) in node.Properties)
        {
            var childPath = SettingPath.Child(path, key);
            if (shape.Find(key, ignoreCase) is { } property)
            {
                if (CheckProperty(value, property, SettingPath.Child(path, property.Name)) is { } checkedValue)
                {
                    properties.Add(KeyValuePair.Create(property.Name, checkedValue));
                }
            }
            else if (shape.FindFileOnly(key, ignoreCase) is { } fileOnly)
            {
                if (CheckFileOnly(value, fileOnly, childPath) is { } checkedValue && fileOnly.Name != "$schema")
                {
                    properties.Add(KeyValuePair.Create(fileOnly.Name, checkedValue));
                }
            }
            else
            {
                Error(ValidationPhase.Shape, childPath, value, $"\"{key}\" is not a setting here.",
                    "Remove it, or check the spelling." + Suggestions.DidYouMean(key, shape.KeyNames));
            }
        }

        return new ConfigObject(node.Origin, properties);
    }

    private ConfigNode? CheckProperty(ConfigNode value, SettingProperty property, string path)
    {
        if (value is not ConfigNull)
        {
            return CheckValue(value, property.Type, path, property);
        }

        if (property.Info.Invariant is { } invariant)
        {
            Error(ValidationPhase.Invariants, path, value, "cannot be removed with null.",
                $"Set a limit; it can be high, but it always exists ({invariant}). Leave the setting out to use the default.");
            return null;
        }

        // Null removes the value, so the next lower layer, or the default, applies (§13).
        return value;
    }

    private ConfigNode? CheckFileOnly(ConfigNode value, FileOnlySettingAttribute key, string path)
    {
        var valid = key.Kind == FileOnlySettingKind.Text
            ? value is ConfigScalar { Kind: JsonValueKind.String }
            : value is ConfigArray array && array.Items.All(item => item is ConfigScalar { Kind: JsonValueKind.String });
        if (!valid)
        {
            Mismatch(path, value, key.Kind == FileOnlySettingKind.Text ? "text" : "a list of text", key.Example);
            return null;
        }

        return value;
    }

    private ConfigNode? CheckValue(ConfigNode node, SettingType type, string path, SettingProperty? property)
    {
        switch (type.Kind)
        {
            case SettingKind.Section when node is ConfigObject section:
                return CheckSection(section, type.Shape!, path);
            case SettingKind.Map when node is ConfigObject map:
                return CheckMap(map, path, (entry, entryPath) => CheckValue(entry, type.Element!, entryPath, null));
            case SettingKind.Capabilities when node is ConfigObject capabilities:
                return CheckCapabilities(capabilities, path);
            case SettingKind.List when node is ConfigArray list:
                return CheckList(list, type, path);
            case SettingKind.ModelReference when node is ConfigObject inline:
                return CheckSection(inline, type.Shape!, path);
            case SettingKind.ModelReference when node is ConfigScalar { Kind: JsonValueKind.String, Raw.Length: > 0 }:
                return node;
            case SettingKind.Secret:
                return CheckSecret(node, path);
            case SettingKind.Text when property?.Info.AllowFile == true && node is ConfigObject include:
                return ReadInclude(include, path);
            case SettingKind.Any:
                return node;
            case SettingKind.Condition:
                var count = errors.Count;
                var conditionErrors = new List<ConfigurationError>();
                ConditionParser.Parse(node.ToElement(), path, conditionErrors);
                foreach (var error in conditionErrors)
                {
                    Error(error.Phase, error.Path, node, error.Problem, error.Fix);
                }

                return errors.Count == count ? node : null;
            default:
                if (node is ConfigScalar scalar && ReadScalar(scalar, type) is { } read)
                {
                    return read;
                }

                var expected = property?.Info.AllowFile == true ? "text, or { \"file\": \"path\" }" : Expected(type);
                Mismatch(path, node, expected, property?.Info.Example ?? ExampleOf(type));
                return null;
        }
    }

    private ConfigObject CheckMap(ConfigObject map, string path, Func<ConfigNode, string, ConfigNode?> checkEntry)
    {
        var entries = new List<KeyValuePair<string, ConfigNode>>();
        foreach (var (name, value) in map.Properties)
        {
            var entryPath = SettingPath.Child(path, name);
            if (!ValidName().IsMatch(name))
            {
                Error(ValidationPhase.Shape, entryPath, value, $"\"{name}\" is not a valid name.",
                    "Use letters, digits, '-' and '_', starting with a letter, digit or '_'.");
            }
            else if (value is ConfigNull)
            {
                entries.Add(KeyValuePair.Create(name, value));
            }
            else if (checkEntry(value, entryPath) is { } checkedValue)
            {
                entries.Add(KeyValuePair.Create(name, checkedValue));
            }
        }

        return new ConfigObject(map.Origin, entries);
    }

    private ConfigObject CheckCapabilities(ConfigObject node, string path)
    {
        var entries = new List<KeyValuePair<string, ConfigNode>>();
        var available = model.Capabilities.All.Select(capability => capability.Name).ToArray();
        foreach (var (key, value) in node.Properties)
        {
            var name = available.FirstOrDefault(candidate => string.Equals(candidate, key, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            var entryPath = SettingPath.Child(path, name ?? key);
            if (name is null)
            {
                Error(ValidationPhase.Shape, entryPath, value, $"capability \"{key}\" is not available in this application.",
                    (available.Length == 0 ? "This application has no capabilities registered." : $"Available capabilities: {string.Join(", ", available)}.") + Suggestions.DidYouMean(key, available));
                continue;
            }

            var shape = model.CapabilityShape(name)!;
            ConfigNode? normal = value switch
            {
                ConfigNull => value,
                ConfigScalar { Kind: JsonValueKind.True or JsonValueKind.False } flag => Switch(flag),
                ConfigScalar { Lenient: true, Kind: JsonValueKind.String } text when bool.TryParse(text.Raw, out var on) =>
                    Switch(new ConfigScalar(text.Origin, on ? JsonValueKind.True : JsonValueKind.False, on ? "true" : "false")),
                ConfigObject settings => CheckSection(settings, shape, entryPath),
                _ => null,
            };

            if (normal is null)
            {
                Mismatch(entryPath, value, "true, false or an object of the capability's settings", "true");
                continue;
            }

            entries.Add(KeyValuePair.Create(name, normal));
        }

        return new ConfigObject(node.Origin, entries);

        // "true" is shorthand for { "enabled": true } (§9).
        static ConfigObject Switch(ConfigScalar flag) => new(flag.Origin, [KeyValuePair.Create("enabled", (ConfigNode)flag)]);
    }

    private ConfigArray? CheckList(ConfigArray list, SettingType type, string path)
    {
        var count = errors.Count;
        var items = new List<ConfigNode>();
        for (var index = 0; index < list.Items.Count; index++)
        {
            var itemPath = SettingPath.Item(path, index);
            var item = list.Items[index];
            if (item is ConfigNull)
            {
                Error(ValidationPhase.Shape, itemPath, item, "a list item cannot be null.", "Remove it.");
            }
            else if (CheckValue(item, type.Element!, itemPath, null) is { } checkedItem)
            {
                items.Add(checkedItem);
            }
        }

        // A list is replaced as a whole, so a list with a bad item is dropped as a whole.
        return errors.Count == count ? new ConfigArray(list.Origin, items) : null;
    }

    private ConfigNode? CheckSecret(ConfigNode node, string path)
    {
        if (node is ConfigObject { Properties: [{ Key: "secret", Value: ConfigScalar { Kind: JsonValueKind.String } name }] })
        {
            if (SecretReference.IsValidName(name.Raw))
            {
                return node;
            }

            Error(ValidationPhase.Shape, SettingPath.Child(path, "secret"), name, $"\"{name.Raw}\" is not a secret name.",
                "A secret name has letters, digits and underscores, such as ANTHROPIC_API_KEY.");
            return null;
        }

        if (node is ConfigScalar { Kind: JsonValueKind.String })
        {
            // The value is not repeated: it may be the secret itself.
            Error(ValidationPhase.Shape, path, node, "is plain text, but a secret is never written in configuration.",
                "Write { \"secret\": \"NAME\" } and put the value in the secret source, such as an environment variable NAME (CFG-09).");
            return null;
        }

        Mismatch(path, node, "a secret reference", "{ \"secret\": \"ANTHROPIC_API_KEY\" }");
        return null;
    }

    private ConfigScalar? ReadInclude(ConfigObject include, string path)
    {
        if (include.Properties is not [{ Key: "file", Value: ConfigScalar { Kind: JsonValueKind.String } file }])
        {
            Mismatch(path, include, "text, or { \"file\": \"path\" }", "{ \"file\": \"prompts/developer.md\" }");
            return null;
        }

        var directory = include.Origin.FullPath is { } containing ? Path.GetDirectoryName(containing)! : baseDirectory;
        var fullPath = Path.GetFullPath(Path.Combine(directory, file.Raw));
        if (!File.Exists(fullPath))
        {
            Error(ValidationPhase.References, SettingPath.Child(path, "file"), file, $"file \"{file.Raw}\" does not exist.",
                $"Paths are relative to the file that contains them; it was looked for at {fullPath}.");
            badPaths.Add(path);
            return null;
        }

        // Normalised to \n so the stable prefix is the same on Linux and Windows (COST-01).
        var text = File.ReadAllText(fullPath, Encoding.UTF8).ReplaceLineEndings("\n");
        return ConfigScalar.Text(text, include.Origin);
    }

    /// <summary>The scalar in the form the setting expects, or null when it does not fit.</summary>
    private static ConfigScalar? ReadScalar(ConfigScalar scalar, SettingType type)
    {
        var text = scalar.Raw;
        var origin = scalar.Origin;
        switch (type.Kind)
        {
            case SettingKind.Text when scalar.Kind == JsonValueKind.String || scalar.Lenient:
                return ConfigScalar.Text(text, origin);
            case SettingKind.Number when scalar.Kind == JsonValueKind.Number || (scalar.Lenient && scalar.TryGetDecimal(out _)):
                return scalar.TryGetDecimal(out _) ? new ConfigScalar(origin, JsonValueKind.Number, text) : null;
            case SettingKind.WholeNumber when scalar.Kind == JsonValueKind.Number || scalar.Lenient:
                var max = type.ClrType == typeof(int) ? int.MaxValue : long.MaxValue;
                var min = type.ClrType == typeof(int) ? int.MinValue : long.MinValue;
                return scalar.TryGetDecimal(out var whole) && decimal.Truncate(whole) == whole && whole <= max && whole >= min
                    ? new ConfigScalar(origin, JsonValueKind.Number, whole.ToString(CultureInfo.InvariantCulture))
                    : null;
            case SettingKind.Boolean when scalar.Kind is JsonValueKind.True or JsonValueKind.False:
                return scalar;
            case SettingKind.Boolean when scalar.Lenient && bool.TryParse(text, out var flag):
                return new ConfigScalar(origin, flag ? JsonValueKind.True : JsonValueKind.False, flag ? "true" : "false");
            case SettingKind.Choice when scalar.Kind == JsonValueKind.String || scalar.Lenient:
                var choice = type.Choices.FirstOrDefault(name => string.Equals(name, text, scalar.Lenient ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                return choice is null ? null : ConfigScalar.Text(choice, origin);
            case SettingKind.Duration when (scalar.Kind == JsonValueKind.String || scalar.Lenient) && Durations.TryParse(text, out _):
                return ConfigScalar.Text(text, origin);
            default:
                return null;
        }
    }

    private void Mismatch(string path, ConfigNode node, string expected, string? example) =>
        Error(ValidationPhase.Shape, path, node, $"is {Describe(node)}, but must be {expected}.",
            example is null ? $"Write {expected}." : $"Write {expected}, such as {example}.");

    private void Error(ValidationPhase phase, string path, ConfigNode node, string problem, string fix)
    {
        errors.Add(new ConfigurationError(phase, path, problem, fix) { Location = node.Origin.Location });
        badPaths.Add(path);
    }

    private static string Describe(ConfigNode node) => node switch
    {
        ConfigObject => "an object",
        ConfigArray => "a list",
        ConfigNull => "null",
        // Text is not repeated, in case it is a secret written in the wrong place.
        ConfigScalar { Kind: JsonValueKind.String } => "text",
        ConfigScalar scalar => scalar.Raw,
        _ => "a value",
    };

    private static string Expected(SettingType type) => type.Kind switch
    {
        SettingKind.Section => "an object of settings",
        SettingKind.Map => "an object of named entries",
        SettingKind.Capabilities => "an object of capabilities",
        SettingKind.List => "a list",
        SettingKind.Text => "text",
        SettingKind.WholeNumber => "a whole number",
        SettingKind.Number => "a number",
        SettingKind.Boolean => "true or false",
        SettingKind.Choice => "one of " + string.Join(", ", type.Choices.Select(choice => $"\"{choice}\"")),
        SettingKind.Duration => "a duration: a number with a unit, ms, s, m, h or d",
        SettingKind.ModelReference => "the name of a model profile, or a profile written inline",
        _ => "a value",
    };

    private static string? ExampleOf(SettingType type) => type.Kind switch
    {
        SettingKind.Duration => "\"30m\"",
        SettingKind.Choice => $"\"{type.Choices[0]}\"",
        _ => null,
    };

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_-]*$")]
    private static partial Regex ValidName();
}
