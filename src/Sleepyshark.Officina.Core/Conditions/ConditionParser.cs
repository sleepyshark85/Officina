using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Configuration.Model;
using Sleepyshark.Officina.Core.Configuration.Validation;

namespace Sleepyshark.Officina.Core.Conditions;

/// <summary>Reads conditions from their file form, reporting every mistake with its path (CFG-13).</summary>
public static class ConditionParser
{
    private const string Grammar =
        "Write { \"field\": \"<path>\", \"<operator>\": <value> } with one of equals, in, gt, gte, lt, lte or exists; or { \"all\": [ … ] }, { \"any\": [ … ] } or { \"not\": … }.";

    private static readonly string[] Combinators = ["all", "any", "not"];

    /// <summary>The condition, or null when it has errors; the errors are added to <paramref name="errors"/>.</summary>
    public static Condition? Parse(JsonElement json, string path, ICollection<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (json.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Error(path, $"a condition is an object, not {ConditionText.Describe(json.ValueKind)}.", Grammar));
            return null;
        }

        var keys = json.EnumerateObject().Select(property => property.Name).ToArray();
        if (keys.Contains("field"))
        {
            return ParseField(json, path, keys, errors);
        }

        if (keys.Length != 1 || !Combinators.Contains(keys[0]))
        {
            var unknown = keys.FirstOrDefault(key => !Combinators.Contains(key));
            errors.Add(Error(path,
                unknown is null ? "a condition has exactly one of all, any or not." : $"\"{unknown}\" is not part of the condition language.",
                Grammar + (unknown is null ? "" : Suggestions.DidYouMean(unknown, [.. Combinators, "field", .. FieldCondition.Keywords.Select(entry => entry.Keyword)]))));
            return null;
        }

        var key = keys[0];
        var body = json.GetProperty(key);
        var childPath = SettingPath.Child(path, key);
        if (key == "not")
        {
            return Parse(body, childPath, errors) is { } inner ? new NotCondition(inner) : null;
        }

        if (body.ValueKind != JsonValueKind.Array || body.GetArrayLength() == 0)
        {
            errors.Add(Error(childPath, $"{key} takes a non-empty list of conditions.", $"Write {{ \"{key}\": [ <condition>, <condition> ] }}."));
            return null;
        }

        var count = errors.Count;
        var parts = body.EnumerateArray().Select((item, index) => Parse(item, SettingPath.Item(childPath, index), errors)).ToArray();
        if (errors.Count > count)
        {
            return null;
        }

        ValueList<Condition> conditions = [.. parts.Select(part => part!)];
        return key == "all" ? new AllCondition(conditions) : new AnyCondition(conditions);
    }

    private static FieldCondition? ParseField(JsonElement json, string path, string[] keys, ICollection<ConfigurationError> errors)
    {
        var field = json.GetProperty("field");
        if (field.ValueKind != JsonValueKind.String || !FieldPath.TryParse(field.GetString()!, out var fieldPath))
        {
            errors.Add(Error(SettingPath.Child(path, "field"), "the field is not a dotted path.", "Write a path such as \"output.items[0].severity\"."));
            return null;
        }

        var operators = keys.Where(key => key != "field").ToArray();
        if (operators.Length != 1 || FieldCondition.Keywords.All(entry => entry.Keyword != operators[0]))
        {
            var unknown = operators.FirstOrDefault(key => FieldCondition.Keywords.All(entry => entry.Keyword != key));
            errors.Add(Error(path,
                unknown is null ? "a field test has exactly one operator." : $"\"{unknown}\" is not an operator.",
                "Use one of equals, in, gt, gte, lt, lte or exists." + (unknown is null ? "" : Suggestions.DidYouMean(unknown, FieldCondition.Keywords.Select(entry => entry.Keyword)))));
            return null;
        }

        var keyword = operators[0];
        var op = FieldCondition.Keywords.First(entry => entry.Keyword == keyword).Operator;
        var operand = json.GetProperty(keyword);
        var operandPath = SettingPath.Child(path, keyword);
        var problem = op switch
        {
            FieldOperator.Exists when operand.ValueKind is not (JsonValueKind.True or JsonValueKind.False) => ("exists takes true or false.", "Write \"exists\": true."),
            FieldOperator.OneOf when operand.ValueKind != JsonValueKind.Array || operand.EnumerateArray().Any(item => !IsScalar(item)) =>
                ("in takes a list of values.", "Write \"in\": [\"bug\", \"regression\"]."),
            FieldOperator.EqualTo when !IsScalar(operand) => ("equals compares with text, a number, true, false or null.", "Compare a field that holds a single value."),
            FieldOperator.GreaterThan or FieldOperator.AtLeast or FieldOperator.LessThan or FieldOperator.AtMost when operand.ValueKind != JsonValueKind.Number =>
                ($"{keyword} compares numbers only.", $"Write \"{keyword}\": 0.8."),
            _ => default((string, string)?),
        };

        if (problem is { } found)
        {
            errors.Add(Error(operandPath, found.Item1, found.Item2));
            return null;
        }

        return new FieldCondition(fieldPath, op, SettingValue.From(operand));
    }

    private static bool IsScalar(JsonElement value) => value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array);

    private static ConfigurationError Error(string path, string problem, string fix) => new(ValidationPhase.Shape, path, problem, fix);
}
