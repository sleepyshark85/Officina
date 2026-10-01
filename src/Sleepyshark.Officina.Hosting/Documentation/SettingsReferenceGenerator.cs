using System.Globalization;
using System.Text;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;

namespace Sleepyshark.Officina.Hosting.Documentation;

/// <summary>
/// Generates the settings reference from the Options classes (DOC-01): every setting with its meaning,
/// allowed values, default and an example, so the documentation cannot drift from the defaults in code.
/// </summary>
public static class SettingsReferenceGenerator
{
    public static string Generate(SettingsModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var text = new StringBuilder();
        text.Append("""
            # Officina — Settings reference

            Generated from the Options classes in `Sleepyshark.Officina.Core` by the test that keeps this file
            current; do not edit it by hand. It lists the settings the code has today. Settings that later slices
            add are specified in the draft [configuration reference](configuration-reference.md).

            How the layers merge, the value forms and validation are described in the configuration reference
            (§2, §13, §14). **Live** settings may be changed by the owner during a run (CFG-08). In files, `null`
            removes a value so the code default applies, except where it would weaken an invariant.

            """).Append('\n');

        var written = new HashSet<Type>();
        var sections = new List<(string Title, ObjectShape Shape)> { ("Top level", model.Root) };
        for (var index = 0; index < sections.Count; index++)
        {
            var (title, shape) = sections[index];
            if (!written.Add(shape.Type))
            {
                continue;
            }

            text.Append(CultureInfo.InvariantCulture, $"## {title}\n\n");
            if (Summary(shape.Type) is { } summary)
            {
                text.Append(summary).Append("\n\n");
            }

            text.Append("| Setting | Allowed values | Default | Description | Example |\n|---|---|---|---|---|\n");
            var defaults = shape.Type.IsAbstract ? null : shape.CreateDefault();
            var prefix = title == "Top level" ? "" : title.Trim('`') + ".";
            foreach (var key in shape.FileOnly)
            {
                text.Append(Row(key.Name, key.Kind == FileOnlySettingKind.Text ? "text" : "list of text", "none", key.Description + " Files only.", key.Example));
            }

            foreach (var property in shape.Properties)
            {
                text.Append(Row(property.Name, Allowed(property.Type, property), Default(property, defaults, model), Description(property), property.Info.Example));
                foreach (var (path, child) in Children(prefix + property.Name, property.Type))
                {
                    sections.Add(($"`{path}`", child));
                }
            }

            text.Append('\n');
        }

        text.Append("## Capabilities\n\n");
        if (model.Capabilities.All.Count == 0)
        {
            text.Append("No capability is available in this build yet. Each capability adds its section here when it ships.\n");
        }

        foreach (var capability in model.Capabilities.All)
        {
            var shape = model.ShapeOf(capability.SettingsType);
            var defaults = shape.CreateDefault();
            text.Append(CultureInfo.InvariantCulture, $"### `capabilities.{capability.Name}`\n\n{capability.Description} `true` is shorthand for `{{ \"enabled\": true }}`.\n\n");
            text.Append("| Setting | Allowed values | Default | Description | Example |\n|---|---|---|---|---|\n");
            foreach (var property in shape.Properties)
            {
                text.Append(Row(property.Name, Allowed(property.Type, property), Default(property, defaults, model), Description(property), property.Info.Example));
            }

            text.Append('\n');
        }

        // Raw string literals take the line endings of the checkout; the reference always has \n.
        return text.ToString().ReplaceLineEndings("\n").TrimEnd('\n') + "\n";
    }

    private static IEnumerable<(string Path, ObjectShape Shape)> Children(string path, SettingType type) => type.Kind switch
    {
        SettingKind.Section => [(path, type.Shape!)],
        SettingKind.Map when type.Element!.Kind == SettingKind.Section => [(path + ".<name>", type.Element.Shape!)],
        SettingKind.List when type.Element!.Kind == SettingKind.Section => [(path + "[]", type.Element.Shape!)],
        _ => [],
    };

    private static string Row(string name, string allowed, string defaultValue, string description, string? example) =>
        $"| `{name}` | {allowed} | {defaultValue} | {Escape(description)} | {(example is null ? "" : Code(example))} |\n";

    private static string Description(SettingProperty property)
    {
        var text = property.Info.Description;
        if (property.Required)
        {
            text += " **Required.**";
        }

        if (property.Info.Live)
        {
            text += " **Live.**";
        }

        if (property.Info.Invariant is { } invariant)
        {
            text += $" Cannot be removed or made unlimited ({invariant}).";
        }

        return text;
    }

    private static string Default(SettingProperty property, object? defaults, SettingsModel model)
    {
        if (property.Required)
        {
            return "none (required)";
        }

        if (property.Type.Kind == SettingKind.Section)
        {
            return "see below";
        }

        var value = defaults is null ? null : property.GetValue(defaults);
        return value is null ? "unset" : Code(OptionsWriter.WriteValue(value, property.Type, model).ToJsonString());
    }

    private static string Allowed(SettingType type, SettingProperty? property) => type.Kind switch
    {
        SettingKind.Section => "section",
        SettingKind.Map => $"named entries, each {Allowed(type.Element!, null)}",
        SettingKind.List => $"list of {Allowed(type.Element!, null)}",
        SettingKind.Text when property?.Info.AllowFile == true => "text, or `{ \"file\": \"path\" }`",
        SettingKind.Text => "text",
        SettingKind.WholeNumber => "whole number" + Range(property),
        SettingKind.Number => "number" + Range(property),
        SettingKind.Boolean => "`true`, `false`",
        SettingKind.Choice => string.Join(", ", type.Choices.Select(choice => $"`\"{choice}\"`")),
        SettingKind.Duration => "duration (`ms`, `s`, `m`, `h`, `d`)" + Range(property),
        SettingKind.Secret => "`{ \"secret\": \"NAME\" }`",
        SettingKind.ModelReference => "profile name, or a profile inline (as `models.<name>`)",
        SettingKind.Capabilities => "see [Capabilities](#capabilities)",
        SettingKind.Condition => "condition (§6)",
        _ => "any JSON value",
    };

    private static string Range(SettingProperty? property)
    {
        if (property is null)
        {
            return "";
        }

        var info = property.Info;
        if (!double.IsNaN(info.Minimum) && info.Minimum == info.Maximum && !info.ExclusiveMinimum)
        {
            return ", only " + info.Minimum.ToString(CultureInfo.InvariantCulture);
        }

        var parts = new List<string>();
        if (!double.IsNaN(info.Minimum))
        {
            parts.Add((info.ExclusiveMinimum ? "> " : "≥ ") + info.Minimum.ToString(CultureInfo.InvariantCulture));
        }

        if (!double.IsNaN(info.Maximum))
        {
            parts.Add("≤ " + info.Maximum.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "" : ", " + string.Join(" and ", parts);
    }

    private static string? Summary(Type type) => type.Name switch
    {
        nameof(OfficinaOptions) => "Every section is optional. Files may also use the keys marked *Files only*.",
        _ => null,
    };

    private static string Code(string json) => "`" + json.Replace("|", "\\|", StringComparison.Ordinal) + "`";

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal);
}
