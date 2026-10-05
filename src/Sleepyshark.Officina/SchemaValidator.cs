using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina;

/// <summary>
/// Validates JSON against the subset of JSON Schema (draft 2020-12) that the core's schemas use (Q2, TOOL-02): those the
/// platform's schema exporter makes for tools and typed output, and those MCP servers commonly send. A schema outside
/// the subset is refused when it is defined, so a schema is never half checked.
/// </summary>
internal static class SchemaValidator
{
    /// <summary>Keywords that only describe, and do not constrain.</summary>
    private static readonly HashSet<string> Annotations =
        ["$schema", "$id", "$comment", "title", "description", "default", "examples", "format", "readOnly", "writeOnly", "deprecated"];

    private static readonly HashSet<string> Types = ["object", "array", "string", "number", "integer", "boolean", "null"];

    private static readonly TimeSpan PatternTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Throws <see cref="ArgumentException"/> if <paramref name="schema"/> uses anything outside the subset.</summary>
    public static void CheckSubset(JsonElement schema, string path = "")
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return;
        }

        Require(schema.ValueKind == JsonValueKind.Object, path, "a schema must be an object or a boolean");
        foreach (var keyword in schema.EnumerateObject())
        {
            var (value, at) = (keyword.Value, $"{path}/{keyword.Name}");
            switch (keyword.Name)
            {
                case var name when Annotations.Contains(name):
                    break;
                case "type":
                    var types = value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : new[] { value };
                    Require(types.Length > 0 && types.All(type => type.ValueKind == JsonValueKind.String && Types.Contains(type.GetString()!)), at, "unknown type");
                    break;
                case "properties":
                    Require(value.ValueKind == JsonValueKind.Object, at, "must be an object");
                    foreach (var property in value.EnumerateObject())
                    {
                        CheckSubset(property.Value, $"{at}/{property.Name}");
                    }

                    break;
                case "required":
                    Require(value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(name => name.ValueKind == JsonValueKind.String), at, "must be an array of names");
                    break;
                case "additionalProperties" or "items":
                    CheckSubset(value, at);
                    break;
                case "anyOf":
                    Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0, at, "must be a non-empty array");
                    var index = 0;
                    foreach (var option in value.EnumerateArray())
                    {
                        CheckSubset(option, $"{at}/{index++}");
                    }

                    break;
                case "enum":
                    Require(value.ValueKind == JsonValueKind.Array, at, "must be an array");
                    break;
                case "const":
                    break;
                case "minLength" or "maxLength" or "minItems" or "maxItems":
                    Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count) && count >= 0, at, "must be a non-negative integer");
                    break;
                case "minimum" or "maximum":
                    Require(value.ValueKind == JsonValueKind.Number, at, "must be a number");
                    break;
                case "pattern":
                    Require(value.ValueKind == JsonValueKind.String, at, "must be a string");
                    _ = new Regex(value.GetString()!, RegexOptions.None, PatternTimeout);
                    break;
                default:
                    Require(false, at, "is not a supported keyword");
                    break;
            }
        }
    }

    /// <summary>Validates <paramref name="value"/> against a schema in the subset; returns the problems, none when it is valid.</summary>
    public static List<string> Validate(JsonElement schema, JsonElement value)
    {
        var problems = new List<string>();
        Validate(schema, value, "", problems);
        return problems;
    }

    private static void Validate(JsonElement schema, JsonElement value, string path, List<string> problems)
    {
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            Check(schema.ValueKind == JsonValueKind.True, "is not allowed");
            return;
        }

        foreach (var keyword in schema.EnumerateObject())
        {
            var rule = keyword.Value;
            switch (keyword.Name)
            {
                case "type":
                    var types = rule.ValueKind == JsonValueKind.Array ? rule.EnumerateArray().Select(type => type.GetString()!).ToList() : [rule.GetString()!];
                    Check(types.Any(type => IsType(value, type)), $"must be {string.Join(" or ", types)}");
                    break;
                case "enum":
                    Check(rule.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value)), $"must be one of {rule.GetRawText()}");
                    break;
                case "const":
                    Check(JsonElement.DeepEquals(rule, value), $"must be {rule.GetRawText()}");
                    break;
                case "anyOf":
                    Check(rule.EnumerateArray().Any(option => Validate(option, value).Count == 0), "matches none of the allowed schemas");
                    break;
                case "properties" when value.ValueKind == JsonValueKind.Object:
                    foreach (var property in rule.EnumerateObject())
                    {
                        if (value.TryGetProperty(property.Name, out var child))
                        {
                            Validate(property.Value, child, $"{path}/{property.Name}", problems);
                        }
                    }

                    break;
                case "required" when value.ValueKind == JsonValueKind.Object:
                    foreach (var name in rule.EnumerateArray().Select(name => name.GetString()!).Where(name => !value.TryGetProperty(name, out _)))
                    {
                        problems.Add($"{path}/{name}: is required");
                    }

                    break;
                case "additionalProperties" when value.ValueKind == JsonValueKind.Object:
                    var known = schema.TryGetProperty("properties", out var properties) ? properties : default;
                    foreach (var property in value.EnumerateObject().Where(property => known.ValueKind != JsonValueKind.Object || !known.TryGetProperty(property.Name, out _)))
                    {
                        Validate(rule, property.Value, $"{path}/{property.Name}", problems);
                    }

                    break;
                case "items" when value.ValueKind == JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Validate(rule, item, $"{path}/{index++}", problems);
                    }

                    break;
                case "minLength" when value.ValueKind == JsonValueKind.String:
                    Check(value.GetString()!.EnumerateRunes().Count() >= rule.GetInt32(), $"must have at least {rule.GetInt32()} characters");
                    break;
                case "maxLength" when value.ValueKind == JsonValueKind.String:
                    Check(value.GetString()!.EnumerateRunes().Count() <= rule.GetInt32(), $"must have at most {rule.GetInt32()} characters");
                    break;
                case "minItems" when value.ValueKind == JsonValueKind.Array:
                    Check(value.GetArrayLength() >= rule.GetInt32(), $"must have at least {rule.GetInt32()} items");
                    break;
                case "maxItems" when value.ValueKind == JsonValueKind.Array:
                    Check(value.GetArrayLength() <= rule.GetInt32(), $"must have at most {rule.GetInt32()} items");
                    break;
                case "minimum" when value.ValueKind == JsonValueKind.Number:
                    Check(value.GetDouble() >= rule.GetDouble(), $"must be at least {rule.GetRawText()}");
                    break;
                case "maximum" when value.ValueKind == JsonValueKind.Number:
                    Check(value.GetDouble() <= rule.GetDouble(), $"must be at most {rule.GetRawText()}");
                    break;
                case "pattern" when value.ValueKind == JsonValueKind.String:
                    Check(Regex.IsMatch(value.GetString()!, rule.GetString()!, RegexOptions.None, PatternTimeout), $"must match {rule.GetString()}");
                    break;
            }
        }

        void Check(bool valid, string problem)
        {
            if (!valid)
            {
                problems.Add($"{(path.Length == 0 ? "/" : path)}: {problem}");
            }
        }
    }

    private static bool IsType(JsonElement value, string type) => (type, value.ValueKind) switch
    {
        ("object", JsonValueKind.Object) or ("array", JsonValueKind.Array) or ("string", JsonValueKind.String) => true,
        ("number", JsonValueKind.Number) or ("null", JsonValueKind.Null) => true,
        ("boolean", JsonValueKind.True or JsonValueKind.False) => true,
        ("integer", JsonValueKind.Number) => value.TryGetDecimal(out var number) ? decimal.Truncate(number) == number : double.IsInteger(value.GetDouble()),
        _ => false,
    };

    private static void Require(bool valid, string path, string problem)
    {
        if (!valid)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The schema at '{(path.Length == 0 ? "/" : path)}' is outside the supported subset: {problem}."));
        }
    }
}
