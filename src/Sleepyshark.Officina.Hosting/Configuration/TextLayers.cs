using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>A setting given as text outside a file: an environment variable or a run option.</summary>
/// <param name="Path">The setting, as dotted names, such as <c>run.budget.cost</c>.</param>
/// <param name="Value">The value. JSON where it parses as JSON, otherwise text.</param>
/// <param name="Source">What set it, such as <c>--budget</c>, shown as its origin.</param>
public sealed record RunOption(string Path, string Value, string Source);

/// <summary>Builds the environment-variable and run-option layers (configuration reference §13).</summary>
internal static class TextLayers
{
    public const string EnvironmentPrefix = "SOF__";

    /// <summary>
    /// Variables of the form <c>SOF__section__setting=value</c>. Setting names are matched ignoring case;
    /// names of agents, models and other named items are matched exactly.
    /// </summary>
    public static ConfigObject? FromEnvironment(IReadOnlyDictionary<string, string> variables, ICollection<ConfigurationError> errors)
    {
        var settings = variables
            .Where(variable => variable.Key.StartsWith(EnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(variable => variable.Key, StringComparer.Ordinal)
            .Select(variable => (Segments: variable.Key[EnvironmentPrefix.Length..].Split("__"), variable.Value,
                Origin: new ConfigOrigin(LayerKind.EnvironmentVariable, variable.Key)));
        return Build(settings, LayerKind.EnvironmentVariable, errors);
    }

    public static ConfigObject? FromRunOptions(IEnumerable<RunOption> options, ICollection<ConfigurationError> errors) =>
        Build(options.Select(option => (option.Path.Split('.'), option.Value, new ConfigOrigin(LayerKind.RunOption, option.Source))), LayerKind.RunOption, errors);

    private static ConfigObject? Build(IEnumerable<(string[] Segments, string Value, ConfigOrigin Origin)> settings, LayerKind layer, ICollection<ConfigurationError> errors)
    {
        var root = new Dictionary<string, object>(StringComparer.Ordinal);
        var any = false;
        foreach (var (segments, value, origin) in settings)
        {
            any = true;
            if (segments.Length == 0 || segments.Any(segment => segment.Length == 0))
            {
                errors.Add(new ConfigurationError(ValidationPhase.Shape, "", $"\"{origin.Source}\" does not name a setting.",
                    "Separate setting names with \"__\" in variables and \".\" in run options, such as SOF__run__permissionMode.") { Location = origin.Location });
                continue;
            }

            var level = root;
            var conflict = false;
            foreach (var segment in segments[..^1])
            {
                if (!level.TryGetValue(segment, out var child))
                {
                    child = new Dictionary<string, object>(StringComparer.Ordinal);
                    level[segment] = child;
                }

                if (child is not Dictionary<string, object> nested)
                {
                    conflict = true;
                    break;
                }

                level = nested;
            }

            if (conflict || level.ContainsKey(segments[^1]))
            {
                errors.Add(new ConfigurationError(ValidationPhase.Shape, string.Join('.', segments), $"\"{origin.Source}\" sets part of a value that another variable or option also sets.",
                    "Set the value once, either whole or by its parts.") { Location = origin.Location });
                continue;
            }

            level[segments[^1]] = Parse(value, origin);
        }

        return any ? ToNode(root, new ConfigOrigin(layer)) : null;
    }

    private static ConfigNode Parse(string value, ConfigOrigin origin)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return Lenient(ConfigNode.FromJson(System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText()), origin));
        }
        catch (JsonException)
        {
            return ConfigScalar.Text(value, origin, lenient: true);
        }
    }

    private static ConfigNode Lenient(ConfigNode node) => node is ConfigScalar scalar
        ? new ConfigScalar(scalar.Origin, scalar.Kind, scalar.Raw, lenient: true)
        : node;

    private static ConfigObject ToNode(Dictionary<string, object> level, ConfigOrigin origin) =>
        new(origin, level.Select(entry => KeyValuePair.Create(entry.Key, entry.Value is Dictionary<string, object> nested ? ToNode(nested, origin) : (ConfigNode)entry.Value)));
}
