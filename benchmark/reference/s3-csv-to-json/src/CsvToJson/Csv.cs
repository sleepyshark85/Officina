using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsvToJson;

/// <summary>A CSV record with the line it starts on.</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

public sealed class CsvException(string message) : Exception(message);

/// <summary>Reads CSV as RFC 4180 defines it, and writes its records as JSON objects keyed by the header.</summary>
public static partial class Csv
{
    public static List<CsvRecord> Parse(string text)
    {
        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var (line, recordLine, i) = (1, 1, 0);
        var quoted = false;
        var atFieldStart = true;
        while (i < text.Length)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }

                i++;
                continue;
            }

            if (c == '"' && atFieldStart)
            {
                quoted = true;
                atFieldStart = false;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
                atFieldStart = true;
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
                continue;
            }
            else if (c == '\n')
            {
                fields.Add(field.ToString());
                field.Clear();
                records.Add(new(recordLine, [.. fields]));
                fields.Clear();
                line++;
                recordLine = line;
                atFieldStart = true;
            }
            else
            {
                field.Append(c);
                atFieldStart = false;
            }

            i++;
        }

        if (quoted)
        {
            throw new CsvException($"line {recordLine}: a quoted field is not closed");
        }

        // A last line with no line break is a record; a last empty line is not.
        if (fields.Count > 0 || field.Length > 0)
        {
            fields.Add(field.ToString());
            records.Add(new(recordLine, [.. fields]));
        }

        return records;
    }

    public static string ToJson(IReadOnlyList<CsvRecord> records, bool types)
    {
        if (records.Count == 0)
        {
            return "[]";
        }

        var header = records[0].Fields;
        foreach (var record in records.Skip(1))
        {
            if (record.Fields.Count != header.Count)
            {
                throw new CsvException($"line {record.Line}: {record.Fields.Count} fields, but the header has {header.Count}");
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var record in records.Skip(1))
            {
                writer.WriteStartObject();
                for (var n = 0; n < header.Count; n++)
                {
                    writer.WritePropertyName(header[n]);
                    WriteValue(writer, record.Fields[n], types);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, string value, bool types)
    {
        if (!types)
        {
            writer.WriteStringValue(value);
        }
        else if (value.Length == 0)
        {
            writer.WriteNullValue();
        }
        else if (value is "true" or "false")
        {
            writer.WriteBooleanValue(value == "true");
        }
        else if (Number().IsMatch(value) && decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    private static partial Regex Number();
}
