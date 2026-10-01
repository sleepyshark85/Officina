using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Conditions;

/// <summary>One step of a field path: a property name or a list index.</summary>
public abstract record FieldSegment;

public sealed record NameSegment(string Name) : FieldSegment;

public sealed record IndexSegment(int Index) : FieldSegment;

/// <summary>A dotted field path with optional <c>[n]</c> indexes, such as <c>output.items[0].severity</c> (CFG-13).</summary>
public sealed partial record FieldPath
{
    private FieldPath(ValueList<FieldSegment> segments)
    {
        Segments = segments;
    }

    /// <summary>The segments; the first is always the root name, such as <c>output</c>.</summary>
    public ValueList<FieldSegment> Segments { get; }

    public string Root => ((NameSegment)Segments[0]).Name;

    public static bool TryParse(string text, out FieldPath path)
    {
        ArgumentNullException.ThrowIfNull(text);
        path = null!;
        if (!Syntax().IsMatch(text))
        {
            return false;
        }

        var segments = new List<FieldSegment>();
        foreach (Match part in Part().Matches(text))
        {
            segments.Add(part.Groups["index"].Success
                ? new IndexSegment(int.Parse(part.Groups["index"].Value, CultureInfo.InvariantCulture))
                : new NameSegment(part.Groups["name"].Value));
        }

        path = new FieldPath(segments);
        return true;
    }

    public override string ToString()
    {
        var text = new StringBuilder();
        foreach (var segment in Segments)
        {
            _ = segment switch
            {
                NameSegment name when text.Length > 0 => text.Append('.').Append(name.Name),
                NameSegment name => text.Append(name.Name),
                IndexSegment index => text.Append('[').Append(index.Index.ToString(CultureInfo.InvariantCulture)).Append(']'),
                _ => text,
            };
        }

        return text.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*(\[[0-9]{1,9}\])*(\.[A-Za-z_][A-Za-z0-9_-]*(\[[0-9]{1,9}\])*)*$")]
    private static partial Regex Syntax();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_-]*)|\[(?<index>[0-9]+)\]")]
    private static partial Regex Part();
}
