using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>
/// What stays the same in every request of a conversation, so the provider can cache it: model settings, sorted tools,
/// instructions, output schema and context management. Its <see cref="Fingerprint"/> identifies it: a conversation is
/// bound to its first run's, and a run with another fails without calling the model. Prefixes are equal when their
/// fingerprints are; the fingerprint is computed when read, so a <c>with</c> copy is never stale.
/// </summary>
/// <param name="ModelSettings">The model's <see cref="IModel.Settings"/>; providers ignore it, knowing their own.</param>
/// <param name="Tools">The tools, in any order, each with its own name; kept sorted by name.</param>
/// <param name="Instructions">The frozen instructions.</param>
/// <param name="OutputSchema">The JSON schema typed output must match, if any.</param>
/// <param name="ContextManagement">How the provider shortens the conversation, if at all.</param>
public sealed record RequestPrefix(
    string ModelSettings, ImmutableArray<Tool> Tools, string Instructions, string? OutputSchema = null, ContextManagement? ContextManagement = null)
{
    public string ModelSettings { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        ModelSettings ?? throw new ArgumentNullException(nameof(ModelSettings));

    /// <summary>Sorted by name, so every request lists them in the same order.</summary>
    public ImmutableArray<Tool> Tools { get; init => field = Sorted(value); } = Sorted(Tools);

    public string Instructions { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        Instructions ?? throw new ArgumentNullException(nameof(Instructions));

    /// <summary>A SHA-256 hash of everything in the prefix that reaches the model.</summary>
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(JsonBytes(writer =>
    {
        // {"model":…,"instructions":…,"tools":[…],"output":…,"contextManagement":{…}}: stored conversations carry the
        // fingerprint, so this encoding never changes.
        writer.WriteStartObject();
        foreach (var (part, json) in Parts())
        {
            writer.WritePropertyName(part);
            writer.WriteRawValue(json);
        }

        writer.WriteEndObject();
    })));

    /// <summary>The parts in which this prefix differs from <paramref name="other"/>, named for people; none when equal.</summary>
    public IReadOnlyList<string> Differences(RequestPrefix other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var (mine, theirs) = (Parts().ToDictionary(), other.Parts().ToDictionary());
        string[] names = ["model", "instructions", "tools", "output", "contextManagement"];
        return [.. names.Where(name => mine.GetValueOrDefault(name) != theirs.GetValueOrDefault(name)).Select(name => name switch
        {
            "model" => "model settings",
            "output" => "output schema",
            "contextManagement" => "context management",
            _ => name,
        })];
    }

    public bool Equals(RequestPrefix? other) => other is not null && Fingerprint == other.Fingerprint;

    public override int GetHashCode() => Fingerprint.GetHashCode(StringComparison.Ordinal);

    /// <summary>Each part as canonical JSON, in fingerprint order. An empty setting asks for nothing, so it is left out.</summary>
    private IEnumerable<(string Part, string Json)> Parts()
    {
        yield return ("model", Json(writer => writer.WriteStringValue(ModelSettings)));
        yield return ("instructions", Json(writer => writer.WriteStringValue(Instructions)));
        yield return ("tools", Json(WriteTools));
        if (OutputSchema is { } schema)
        {
            yield return ("output", Json(writer => writer.WriteRawValue(schema)));
        }

        if (ContextManagement is { } context && (context.CompactAt is not null || context.ClearToolResults is not null))
        {
            yield return ("contextManagement", Json(writer => WriteContextManagement(writer, context)));
        }
    }

    /// <summary>The tools sorted by name; throws <see cref="ArgumentException"/> when two share a name.</summary>
    internal static ImmutableArray<Tool> Sorted(ImmutableArray<Tool> tools)
    {
        var sorted = tools.IsDefault ? [] : tools.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index].Name == sorted[index - 1].Name)
            {
                throw new ArgumentException($"Two tools are named '{sorted[index].Name}'.", nameof(tools));
            }
        }

        return sorted;
    }

    private void WriteTools(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var tool in Tools)
        {
            writer.WriteStartObject();
            writer.WriteString("name", tool.Name);
            writer.WriteString("description", tool.Description);
            writer.WritePropertyName("inputSchema");
            writer.WriteRawValue(tool.InputSchema);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteContextManagement(Utf8JsonWriter writer, ContextManagement context)
    {
        writer.WriteStartObject();
        if (context.CompactAt is { } compactAt)
        {
            writer.WriteNumber("compactAt", compactAt);
        }

        if (context.ClearToolResults is { } clearing)
        {
            writer.WriteNumber("clearAfter", clearing.After);
            writer.WriteNumber("clearKeep", clearing.Keep);
            writer.WriteNumber("clearAtLeastTokens", clearing.AtLeastTokens);
        }

        writer.WriteEndObject();
    }

    private static string Json(Action<Utf8JsonWriter> write) => System.Text.Encoding.UTF8.GetString(JsonBytes(write));

    private static byte[] JsonBytes(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return buffer.ToArray();
    }
}
