using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>
/// The part of every request that stays the same for a whole conversation, so the provider can cache it (CTX-01): the
/// model's settings, the tools sorted by name, the frozen instructions, the typed output schema and how the provider
/// shortens the conversation. Its <see cref="Fingerprint"/> identifies it: a conversation is bound to the fingerprint of
/// its first run, and a run with another fails without calling the model (CTX-04). Two prefixes are equal when their
/// fingerprints are; the fingerprint is computed when read, so a copy made with <c>with</c> is never stale.
/// </summary>
/// <param name="ModelSettings">The model's <see cref="IModel.Settings"/>; a provider ignores it, as it knows its own.</param>
/// <param name="Tools">The tools, in any order; the prefix keeps them sorted by name.</param>
/// <param name="Instructions">The frozen instructions.</param>
/// <param name="OutputSchema">The JSON schema typed output must match, if the agent requires typed output (OUT-01).</param>
/// <param name="ContextManagement">How the provider shortens the conversation, if at all (HIST-01, HIST-02).</param>
public sealed record RequestPrefix(
    string ModelSettings, ImmutableArray<Tool> Tools, string Instructions, string? OutputSchema = null, ContextManagement? ContextManagement = null)
{
    public string ModelSettings { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        ModelSettings ?? throw new ArgumentNullException(nameof(ModelSettings));

    /// <summary>The tools, sorted by name so every request lists them in the same order (CTX-01).</summary>
    public ImmutableArray<Tool> Tools { get; init => field = Sorted(value); } = Sorted(Tools);

    public string Instructions { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } =
        Instructions ?? throw new ArgumentNullException(nameof(Instructions));

    /// <summary>A SHA-256 hash of everything in the prefix that reaches the model; equal prefixes have equal fingerprints.</summary>
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

    /// <summary>
    /// The parts in which this prefix differs from <paramref name="other"/>, named for people (such as <c>tools</c> or
    /// <c>output schema</c>); none when the fingerprints are equal.
    /// </summary>
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

    private static ImmutableArray<Tool> Sorted(ImmutableArray<Tool> tools) =>
        tools.IsDefault ? [] : tools.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));

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
