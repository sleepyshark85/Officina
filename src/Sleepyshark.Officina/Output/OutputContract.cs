using System.Text.Json;
using System.Text.Json.Schema;

namespace Sleepyshark.Officina;

/// <summary>
/// The typed output an agent requires: an application type whose JSON schema the model is held to, and whose instance
/// a completed run returns as <see cref="Completed.Output"/>. The schema is exported like a typed function's, and is
/// part of the cached prefix.
/// </summary>
public sealed class OutputContract
{
    private OutputContract(Type type, JsonElement schema)
    {
        Type = type;
        Schema = schema.GetRawText();
        Parsed = schema;
    }

    /// <summary>The application type the reply is deserialized into.</summary>
    public Type Type { get; }

    /// <summary>The type's JSON schema, as exported; the provider may adjust it to what it accepts.</summary>
    public string Schema { get; }

    internal JsonElement Parsed { get; }

    /// <summary>A contract for <typeparamref name="T"/>; see <see cref="For(System.Type)"/>.</summary>
    public static OutputContract For<T>() => For(typeof(T));

    /// <summary>
    /// A contract for <paramref name="type"/>. Throws <see cref="ArgumentException"/> when its schema is outside the subset
    /// the core validates, such as a recursive type's, or holds an open object, such as a dictionary, which structured
    /// output cannot express.
    /// </summary>
    /// <remarks>Uses reflection, so it is not trim or AOT safe.</remarks>
    public static OutputContract For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var schema = JsonSerializer.SerializeToElement(TypedJson.Options.GetJsonSchemaAsNode(type, TypedJson.Exporter));
        SchemaValidator.CheckSubset(schema);
        RequireClosed(schema, "");
        return new OutputContract(type, schema);
    }

    /// <summary>Validates and deserializes a reply; returns the instance, or the errors.</summary>
    internal (object? Value, string? Error) Read(string text)
    {
        try
        {
            using var reply = JsonDocument.Parse(text);
            var problems = SchemaValidator.Validate(Parsed, reply.RootElement);
            return problems.Count > 0
                ? (null, $"The output does not match its schema: {string.Join("; ", problems)}")
                : (reply.RootElement.Deserialize(Type, TypedJson.Options), null);
        }
#pragma warning disable CA1031 // Whatever deserializing throws, its constructors included, fails the run.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return (null, $"The output could not be read as {Type.Name}: {exception.Message}");
        }
    }

    /// <summary>Throws <see cref="ArgumentException"/> if an object in <paramref name="schema"/> allows unnamed properties.</summary>
    private static void RequireClosed(JsonElement schema, string path)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind != JsonValueKind.False)
        {
            throw new ArgumentException($"The output schema at '{(path.Length == 0 ? "/" : path)}' is an open object, such as a dictionary's; typed output needs objects with named members.");
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                RequireClosed(property.Value, $"{path}/properties/{property.Name}");
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            RequireClosed(items, $"{path}/items");
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var index = 0;
            foreach (var option in anyOf.EnumerateArray())
            {
                RequireClosed(option, $"{path}/anyOf/{index++}");
            }
        }
    }
}
