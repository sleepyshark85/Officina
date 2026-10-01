using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Core.Conditions;

/// <summary>
/// Checks a condition against the values it reads (validation phase 8): every field must exist in the
/// schema of its root, comparisons must fit the field's type, and free text is never read (INV-01).
/// </summary>
public static class ConditionChecker
{
    public static IReadOnlyList<ConfigurationError> Check(Condition condition, ConditionScope scope, string path)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(scope);
        var errors = new List<ConfigurationError>();
        Visit(condition, scope, path, errors);
        return errors;
    }

    private static void Visit(Condition condition, ConditionScope scope, string path, List<ConfigurationError> errors)
    {
        switch (condition)
        {
            case AllCondition all:
                for (var index = 0; index < all.Conditions.Count; index++)
                {
                    Visit(all.Conditions[index], scope, SettingPath.Item(SettingPath.Child(path, "all"), index), errors);
                }

                break;
            case AnyCondition any:
                for (var index = 0; index < any.Conditions.Count; index++)
                {
                    Visit(any.Conditions[index], scope, SettingPath.Item(SettingPath.Child(path, "any"), index), errors);
                }

                break;
            case NotCondition not:
                Visit(not.Condition, scope, SettingPath.Child(path, "not"), errors);
                break;
            case FieldCondition field:
                if (CheckField(field, scope) is { } found)
                {
                    errors.Add(new ConfigurationError(ValidationPhase.Conditions, SettingPath.Child(path, "field"), found.Problem, found.Fix));
                }

                break;
        }
    }

    private static (string Problem, string Fix)? CheckField(FieldCondition condition, ConditionScope scope)
    {
        var field = condition.Field;
        var source = scope.Find(field.Root);
        if (source is null)
        {
            return ($"field \"{field}\" starts with \"{field.Root}\", which conditions cannot read here.",
                $"Use one of: {string.Join(", ", scope.Roots)}.{Suggestions.DidYouMean(field.Root, scope.Roots)}");
        }

        if (source.FreeText)
        {
            return ($"field \"{field}\" reads free text. Decisions are made on structured values only (INV-01).",
                $"Give the step structured output with a schema, and test a field of it.");
        }

        if (source.Schema is not { } schema)
        {
            return null;
        }

        var reached = field.Root;
        foreach (var segment in field.Segments.Skip(1))
        {
            var next = Descend(schema, segment, out var unknownShape);
            if (unknownShape)
            {
                return null;
            }

            if (next is null)
            {
                return segment is NameSegment name
                    ? ($"field \"{field}\" does not exist: {reached} has no \"{name.Name}\".", "Use a field of the schema." + Suggestions.DidYouMean(name.Name, PropertyNames(schema)))
                    : ($"field \"{field}\" does not exist: {reached} is not a list.", "Remove the index.");
            }

            schema = next.Value;
            reached += segment is NameSegment named ? "." + named.Name : $"[{((IndexSegment)segment).Index}]";
        }

        return CheckOperand(condition, schema);
    }

    private static (string Problem, string Fix)? CheckOperand(FieldCondition condition, JsonElement schema)
    {
        var types = Types(schema);
        var field = condition.Field;
        if (condition.Operator is FieldOperator.GreaterThan or FieldOperator.AtLeast or FieldOperator.LessThan or FieldOperator.AtMost)
        {
            return types.Count == 0 || types.Overlaps(["number", "integer"])
                ? null
                : ($"field \"{field}\" is {string.Join(" or ", types)}, so {condition.Keyword} cannot compare it.", "Compare a number field, or use equals or in.");
        }

        IEnumerable<JsonElement> operands = condition.Operator switch
        {
            FieldOperator.EqualTo => [condition.Operand.Element],
            FieldOperator.OneOf => condition.Operand.Element.EnumerateArray(),
            _ => [],
        };

        foreach (var operand in operands)
        {
            var operandType = TypeOf(operand);
            if (types.Count > 0 && !types.Contains(operandType) && !(operandType == "integer" && types.Contains("number")))
            {
                return ($"field \"{field}\" is {string.Join(" or ", types)}, but is compared with {operand.GetRawText()}, so the condition can never hold.", "Compare with a value of the field's type.");
            }

            if (schema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array && !allowed.EnumerateArray().Any(value => FieldCondition.SameValue(value, operand)))
            {
                return ($"field \"{field}\" is never {operand.GetRawText()}: its values are {string.Join(", ", allowed.EnumerateArray().Select(value => value.GetRawText()))}.", "Compare with one of its values.");
            }
        }

        return null;
    }

    /// <summary>The schema of a child, or null when the schema says it does not exist.</summary>
    private static JsonElement? Descend(JsonElement schema, FieldSegment segment, out bool unknownShape)
    {
        unknownShape = false;
        var types = Types(schema);
        if (segment is NameSegment name)
        {
            if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name.Name, out var child))
            {
                return child;
            }

            if (schema.TryGetProperty("additionalProperties", out var additional))
            {
                if (additional.ValueKind == JsonValueKind.Object)
                {
                    return additional;
                }

                unknownShape = additional.ValueKind == JsonValueKind.True;
                return null;
            }

            // Without properties or a type, the schema says nothing about the shape.
            unknownShape = !schema.TryGetProperty("properties", out _) && (types.Count == 0 || types.Contains("object"));
            return null;
        }

        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            return items;
        }

        unknownShape = types.Count == 0 || types.Contains("array");
        return null;
    }

    private static HashSet<string> Types(JsonElement schema)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        if (schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("type", out var type))
        {
            if (type.ValueKind == JsonValueKind.String)
            {
                types.Add(type.GetString()!);
            }
            else if (type.ValueKind == JsonValueKind.Array)
            {
                types.UnionWith(type.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
            }
        }

        return types;
    }

    private static IEnumerable<string> PropertyNames(JsonElement schema) =>
        schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? properties.EnumerateObject().Select(property => property.Name)
            : [];

    private static string TypeOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => IsWhole(value) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        _ => "object",
    };

    private static bool IsWhole(JsonElement number) => number.TryGetDecimal(out var value) && decimal.Truncate(value) == value;
}
