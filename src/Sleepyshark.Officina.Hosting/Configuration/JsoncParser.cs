using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>
/// Reads a configuration file: JSON with comments and trailing commas (configuration reference §2),
/// keeping the line and column of every value.
/// </summary>
internal static partial class JsoncParser
{
    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 64,
    };

    /// <summary>The file's root object, or null with a parse error added.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="origin">The origin of the file itself; values get its layer and source with their own position.</param>
    /// <param name="errors">Where parse errors are added.</param>
    public static ConfigObject? Parse(string text, ConfigOrigin origin, ICollection<ConfigurationError> errors)
    {
        var bytes = Encoding.UTF8.GetBytes(text.TrimStart('﻿'));
        var lines = LineStarts(bytes);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var known = errors.Count;
        try
        {
            if (!reader.Read())
            {
                errors.Add(Error(origin, 1, 1, "the file is empty.", "Write a JSON object, such as {}."));
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                var (line, column) = Position(bytes, lines, reader.TokenStartIndex);
                errors.Add(Error(origin, line, column, "the file is not a JSON object.", "Put the settings in { … }."));
                return null;
            }

            var root = (ConfigObject)ReadValue(ref reader, bytes, lines, origin, errors)!;

            // Anything after the object makes the reader throw.
            reader.Read();
            return errors.Count == known ? root : null;
        }
        catch (JsonException exception)
        {
            errors.Add(Error(origin, (int)(exception.LineNumber ?? 0) + 1, (int)(exception.BytePositionInLine ?? 0) + 1,
                $"the file is not valid JSON: {Clean(exception.Message)}",
                "Fix the syntax there. Comments (//, /* */) and trailing commas are allowed."));
            return null;
        }
    }

    private static ConfigNode? ReadValue(ref Utf8JsonReader reader, byte[] bytes, int[] lines, ConfigOrigin file, ICollection<ConfigurationError> errors)
    {
        var (line, column) = Position(bytes, lines, reader.TokenStartIndex);
        var origin = new ConfigOrigin(file.Layer, file.Source, line, column) { FullPath = file.FullPath };
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var properties = new List<KeyValuePair<string, ConfigNode>>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var key = reader.GetString()!;
                    var (keyLine, keyColumn) = Position(bytes, lines, reader.TokenStartIndex);
                    reader.Read();
                    var value = ReadValue(ref reader, bytes, lines, file, errors);
                    if (properties.Any(property => property.Key == key))
                    {
                        errors.Add(Error(file, keyLine, keyColumn, $"\"{key}\" appears twice in the same object.", "Keep one of them."));
                    }
                    else if (value is not null)
                    {
                        properties.Add(KeyValuePair.Create(key, value));
                    }
                }

                return new ConfigObject(origin, properties);
            case JsonTokenType.StartArray:
                var items = new List<ConfigNode>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (ReadValue(ref reader, bytes, lines, file, errors) is { } item)
                    {
                        items.Add(item);
                    }
                }

                return new ConfigArray(origin, items);
            case JsonTokenType.String:
                return ConfigScalar.Text(reader.GetString()!, origin);
            case JsonTokenType.Number:
                return new ConfigScalar(origin, JsonValueKind.Number, Encoding.UTF8.GetString(reader.ValueSpan));
            case JsonTokenType.True:
                return new ConfigScalar(origin, JsonValueKind.True, "true");
            case JsonTokenType.False:
                return new ConfigScalar(origin, JsonValueKind.False, "false");
            default:
                return new ConfigNull(origin);
        }
    }

    private static int[] LineStarts(byte[] bytes)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return [.. starts];
    }

    private static (int Line, int Column) Position(byte[] bytes, int[] lineStarts, long offset)
    {
        var index = Array.BinarySearch(lineStarts, (int)offset);
        var line = index >= 0 ? index : ~index - 1;
        var column = Encoding.UTF8.GetCharCount(bytes, lineStarts[line], (int)offset - lineStarts[line]) + 1;
        return (line + 1, column);
    }

    private static ConfigurationError Error(ConfigOrigin file, int line, int column, string problem, string fix) =>
        new(ValidationPhase.Parse, "", problem, fix) { Location = new ConfigOrigin(file.Layer, file.Source, line, column).Location };

    private static string Clean(string message) => ReaderPosition().Replace(message, "").Trim();

    [GeneratedRegex(@"\s*(Path: \S*)?\s*\|?\s*LineNumber: \d+ \| BytePositionInLine: \d+\.?")]
    private static partial Regex ReaderPosition();
}
