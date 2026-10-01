using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Conditions;

/// <summary>
/// A condition in the fixed condition language (CFG-13): field access, equality, membership, numeric
/// comparison, presence, and <c>all</c> / <c>any</c> / <c>not</c>. It has no variables, loops,
/// functions or side effects, and reads structured values only.
/// </summary>
public abstract record Condition
{
    /// <summary>Evaluates the condition over the structured values, a JSON object keyed by field root (<c>output</c>, <c>checks</c>, …).</summary>
    public abstract bool Evaluate(JsonElement values);

    /// <summary>The condition in the file form.</summary>
    public abstract JsonNode ToJson();

    public override string ToString() => ToJson().ToJsonString();
}

/// <summary>The operators of a field test, by their keyword in files.</summary>
public enum FieldOperator
{
    /// <summary><c>equals</c></summary>
    EqualTo,

    /// <summary><c>in</c>: equal to one of a list.</summary>
    OneOf,

    /// <summary><c>gt</c></summary>
    GreaterThan,

    /// <summary><c>gte</c></summary>
    AtLeast,

    /// <summary><c>lt</c></summary>
    LessThan,

    /// <summary><c>lte</c></summary>
    AtMost,

    /// <summary><c>exists</c>: present and not null (or, with <c>false</c>, absent or null).</summary>
    Exists,
}

/// <summary>A test of one field, such as <c>{ "field": "output.score", "gte": 0.8 }</c>.</summary>
public sealed record FieldCondition(FieldPath Field, FieldOperator Operator, SettingValue Operand) : Condition
{
    internal static readonly (string Keyword, FieldOperator Operator)[] Keywords =
    [
        ("equals", FieldOperator.EqualTo), ("in", FieldOperator.OneOf), ("gt", FieldOperator.GreaterThan),
        ("gte", FieldOperator.AtLeast), ("lt", FieldOperator.LessThan), ("lte", FieldOperator.AtMost), ("exists", FieldOperator.Exists),
    ];

    public string Keyword => Keywords.First(entry => entry.Operator == Operator).Keyword;

    public override bool Evaluate(JsonElement values)
    {
        var found = Resolve(values, Field);
        var present = found is { ValueKind: not JsonValueKind.Null };
        var operand = Operand.Element;
        return Operator switch
        {
            FieldOperator.Exists => present == (operand.ValueKind == JsonValueKind.True),
            FieldOperator.EqualTo => found is { } value && SameValue(value, operand),
            FieldOperator.OneOf => found is { } value && operand.ValueKind == JsonValueKind.Array && operand.EnumerateArray().Any(item => SameValue(value, item)),
            _ => found is { ValueKind: JsonValueKind.Number } number && operand.ValueKind == JsonValueKind.Number && Compare(number, operand),
        };
    }

    public override JsonNode ToJson() => new JsonObject
    {
        ["field"] = Field.ToString(),
        [Keyword] = JsonNode.Parse(Operand.Element.GetRawText()),
    };

    private bool Compare(JsonElement value, JsonElement operand)
    {
        var order = value.GetDouble().CompareTo(operand.GetDouble());
        return Operator switch
        {
            FieldOperator.GreaterThan => order > 0,
            FieldOperator.AtLeast => order >= 0,
            FieldOperator.LessThan => order < 0,
            _ => order <= 0,
        };
    }

    /// <summary>JSON equality, with numbers compared by value, so <c>1</c> equals <c>1.0</c>.</summary>
    internal static bool SameValue(JsonElement a, JsonElement b)
    {
        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            return a.TryGetDecimal(out var x) && b.TryGetDecimal(out var y) ? x == y : a.GetDouble().Equals(b.GetDouble());
        }

        return JsonElement.DeepEquals(a, b);
    }

    private static JsonElement? Resolve(JsonElement values, FieldPath path)
    {
        var current = values;
        foreach (var segment in path.Segments)
        {
            switch (segment)
            {
                case NameSegment name when current.ValueKind == JsonValueKind.Object && current.TryGetProperty(name.Name, out var child):
                    current = child;
                    break;
                case IndexSegment index when current.ValueKind == JsonValueKind.Array && index.Index < current.GetArrayLength():
                    current = current[index.Index];
                    break;
                default:
                    return null;
            }
        }

        return current;
    }
}

/// <summary><c>{ "all": [ … ] }</c>: every condition holds.</summary>
public sealed record AllCondition(ValueList<Condition> Conditions) : Condition
{
    public override bool Evaluate(JsonElement values) => Conditions.All(condition => condition.Evaluate(values));

    public override JsonNode ToJson() => new JsonObject { ["all"] = new JsonArray([.. Conditions.Select(condition => condition.ToJson())]) };
}

/// <summary><c>{ "any": [ … ] }</c>: at least one condition holds.</summary>
public sealed record AnyCondition(ValueList<Condition> Conditions) : Condition
{
    public override bool Evaluate(JsonElement values) => Conditions.Any(condition => condition.Evaluate(values));

    public override JsonNode ToJson() => new JsonObject { ["any"] = new JsonArray([.. Conditions.Select(condition => condition.ToJson())]) };
}

/// <summary><c>{ "not": … }</c></summary>
public sealed record NotCondition(Condition Condition) : Condition
{
    public override bool Evaluate(JsonElement values) => !Condition.Evaluate(values);

    public override JsonNode ToJson() => new JsonObject { ["not"] = Condition.ToJson() };
}

internal static class ConditionText
{
    public static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "a list",
        JsonValueKind.String => "text",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "true or false",
        _ => "null",
    };
}
