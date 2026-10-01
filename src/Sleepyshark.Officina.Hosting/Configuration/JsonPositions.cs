using System.Text;
using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// One pass over a file with <see cref="Utf8JsonReader"/>: checks the syntax (comments and trailing commas are
/// allowed) and duplicate keys, and records the line and column of every value by its setting path.
/// </summary>
internal static class JsonPositions
{
    private static readonly JsonReaderOptions ReaderOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The positions, or null with a parse error added.</summary>
    public static Dictionary<string, (int Line, int Column)>? Read(string text, ConfigurationOrigin file, LoadErrors errors)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var lineStarts = new List<int> { 0 };
        lineStarts.AddRange(bytes.Select((value, index) => (value, index)).Where(entry => entry.value == '\n').Select(entry => entry.index + 1));

        var positions = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        var containers = new Stack<(string Path, HashSet<string>? Keys, int NextIndex)>();
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        string? pendingKey = null;
        try
        {
            while (reader.Read())
            {
                var position = Position(bytes, lineStarts, (int)reader.TokenStartIndex);
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    pendingKey = reader.GetString()!;
                    if (!containers.Peek().Keys!.Add(pendingKey))
                    {
                        errors.Add(ValidationPhase.Parse, "", $"\"{pendingKey}\" appears twice in the same object.", "Keep one of them.",
                            file with { Line = position.Line, Column = position.Column });
                        return null;
                    }

                    continue;
                }

                if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
                {
                    containers.Pop();
                    continue;
                }

                var path = "";
                if (containers.TryPop(out var parent))
                {
                    path = parent.Keys is null ? $"{parent.Path}[{parent.NextIndex}]" : SettingPaths.Join(parent.Path, pendingKey!);
                    containers.Push(parent with { NextIndex = parent.NextIndex + 1 });
                    positions[path] = position;
                }

                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    containers.Push((path, new HashSet<string>(StringComparer.Ordinal), 0));
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    containers.Push((path, null, 0));
                }
            }
        }
        catch (JsonException exception)
        {
            errors.Add(ValidationPhase.Parse, "", "the file is not valid JSON.",
                "Fix the syntax there. Comments (//, /* */) and trailing commas are allowed.",
                file with { Line = (int)(exception.LineNumber ?? 0) + 1, Column = (int)(exception.BytePositionInLine ?? 0) + 1 });
            return null;
        }

        return positions;
    }

    private static (int Line, int Column) Position(byte[] bytes, List<int> lineStarts, int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        var line = index >= 0 ? index : ~index - 1;
        return (line + 1, Encoding.UTF8.GetCharCount(bytes, lineStarts[line], offset - lineStarts[line]) + 1);
    }
}
