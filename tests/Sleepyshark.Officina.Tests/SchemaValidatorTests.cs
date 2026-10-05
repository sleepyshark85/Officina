using System.Text.Json;
using CsCheck;
using Json.Schema;

namespace Sleepyshark.Officina.Tests;

/// <summary>The schema validator (Q2, TOOL-02), and TEST-08: it agrees with an established validator on the subset.</summary>
public class SchemaValidatorTests
{
    private static readonly Gen<string> Number = Gen.OneOfConst("0", "1", "-2", "3", "1.5", "2.0", "-0.5", "100");

    private static readonly Gen<string> Text = Gen.OneOfConst("\"\"", "\"a\"", "\"ab\"", "\"abc\"", "\"123\"", "\"b1\"", "\"Ab c\"");

    private static readonly Gen<string> Name = Gen.OneOfConst("a", "b", "c");

    /// <summary>JSON values, at most <paramref name="depth"/> levels of objects and arrays deep.</summary>
    private static Gen<string> Value(int depth) => depth == 0
        ? Gen.OneOf(Gen.OneOfConst("null", "true", "false"), Number, Text)
        : Gen.OneOf(
            Value(0),
            Value(0),
            Value(depth - 1).Array[0, 3].Select(items => $"[{string.Join(",", items)}]"),
            Gen.Select(Name, Value(depth - 1)).Array[0, 3].Select(properties => Object(properties.DistinctBy(property => property.Item1))));

    /// <summary>Schemas in the subset, at most <paramref name="depth"/> levels deep.</summary>
    private static Gen<string> Schema(int depth)
    {
        var type = Gen.OneOfConst("string", "number", "integer", "boolean", "null", "object", "array");
        var leaves = Gen.OneOf(
            Gen.OneOfConst("true", "false", "{}", """{"description":"anything","format":"date"}"""),
            type.Select(name => $$"""{"type":"{{name}}"}"""),
            Gen.Select(type, type).Select(names => $$"""{"type":["{{names.Item1}}","{{names.Item2}}"]}"""),
            Gen.Select(Gen.Int[0, 3], Gen.Int[0, 3]).Select(lengths => $$"""{"type":"string","minLength":{{lengths.Item1}},"maxLength":{{lengths.Item2}}}"""),
            Gen.OneOfConst("^a", "b$", "^[0-9]+$", "a.c").Select(pattern => $$"""{"pattern":"{{pattern}}"}"""),
            Gen.Select(Number, Number).Select(bounds => $$"""{"minimum":{{bounds.Item1}},"maximum":{{bounds.Item2}}}"""),
            Value(1).Array[1, 3].Select(options => $$"""{"enum":[{{string.Join(",", options)}}]}"""),
            Value(1).Select(value => $$"""{"const":{{value}}}"""));
        if (depth == 0)
        {
            return leaves;
        }

        var inner = Schema(depth - 1);
        return Gen.OneOf(
            leaves,
            Gen.Select(Gen.Select(Name, inner).Array[0, 3], Name.Array[0, 3], Gen.OneOf(Gen.OneOfConst("", "true", "false"), inner))
                .Select(parts =>
                {
                    var (properties, required, additional) = parts;
                    var members = new List<string> { "\"type\":\"object\"", $"\"properties\":{Object(properties.DistinctBy(property => property.Item1))}" };
                    members.Add($"\"required\":[{string.Join(",", required.Distinct().Select(name => $"\"{name}\""))}]");
                    if (additional.Length > 0)
                    {
                        members.Add($"\"additionalProperties\":{additional}");
                    }

                    return $"{{{string.Join(",", members)}}}";
                }),
            Gen.Select(inner, Gen.Int[0, 2], Gen.Int[1, 3]).Select(parts => $$"""{"type":"array","items":{{parts.Item1}},"minItems":{{parts.Item2}},"maxItems":{{parts.Item3}}}"""),
            inner.Array[1, 3].Select(options => $$"""{"anyOf":[{{string.Join(",", options)}}]}"""));
    }

    private static string Object(IEnumerable<(string Name, string Value)> properties) =>
        $"{{{string.Join(",", properties.Select(property => $"\"{property.Name}\":{property.Value}"))}}}";

    [Fact]
    public async Task On_schemas_in_the_subset_it_accepts_and_rejects_what_an_established_validator_does()
    {
        var accepted = 0;
        await Property.CheckAsync(
            Gen.Select(Schema(2), Value(2)),
            pair =>
            {
                var (schema, value) = pair;
                using var schemaDocument = JsonDocument.Parse(schema);
                using var valueDocument = JsonDocument.Parse(value);
                SchemaValidator.CheckSubset(schemaDocument.RootElement);

                var ours = SchemaValidator.Validate(schemaDocument.RootElement, valueDocument.RootElement);
                // Draft 2020-12, the subset's dialect, where format is an annotation only.
                var reference = $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","allOf":[{{schema}}]}""";
                var theirs = JsonSchema.FromText(reference).Evaluate(valueDocument.RootElement).IsValid;

                Assert.True(theirs == (ours.Count == 0), $"The reference says {(theirs ? "valid" : "invalid")}; ours found: {string.Join("; ", ours)}");
                accepted += theirs ? 1 : 0;
                return Task.CompletedTask;
            },
            pair => $"schema {pair.Item1} value {pair.Item2}",
            cases: 2_000);

        // The cases test both answers, not mostly one.
        Assert.InRange(accepted, 400, 1_600);
    }

    [Fact]
    public void Problems_name_where_the_input_is_wrong()
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"lines":{"type":"array","items":{"type":"object","properties":{"copies":{"type":"integer","minimum":1}},"required":["isbn"]}}},"additionalProperties":false}""");
        using var input = JsonDocument.Parse("""{"lines":[{"copies":0}],"extra":1}""");

        var problems = SchemaValidator.Validate(schema.RootElement, input.RootElement);

        Assert.Equal(["/lines/0/copies: must be at least 1", "/lines/0/isbn: is required", "/extra: is not allowed"], problems);
    }
}
