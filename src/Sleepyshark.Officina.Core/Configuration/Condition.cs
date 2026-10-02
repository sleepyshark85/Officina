using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// A condition in the fixed condition language (CFG-13): one test on a field, or <c>all</c>, <c>any</c> or <c>not</c>
/// of other conditions. It reads structured values only, and has no variables, functions or side effects. The field
/// roots so far are <c>args</c>, the tool's arguments, and <c>output</c>, a step's structured output; the task board
/// adds its own.
/// </summary>
public sealed partial record Condition
{
    [Setting("The field a test reads: a dotted path with optional `[n]` indexes, such as `args.branch` in a tool's arguments or `output.kind` in a step's output.", Example = "\"args.branch\"")]
    public string? Field { get; init; }

    [Setting("Holds when the field equals this value. Numbers compare by value, and `true` and `false` match booleans.", Example = "\"main\"")]
    public string? Is { get; init; }

    [Setting("Holds when the field equals one of these values.", Example = """["main", "master"]""")]
    public IReadOnlyList<string>? In { get; init; }

    [Setting("Holds when the field is a number greater than this.", Example = "10")]
    public decimal? Gt { get; init; }

    [Setting("Holds when the field is a number greater than or equal to this.", Example = "0.8")]
    public decimal? Gte { get; init; }

    [Setting("Holds when the field is a number less than this.", Example = "10")]
    public decimal? Lt { get; init; }

    [Setting("Holds when the field is a number less than or equal to this.", Example = "100")]
    public decimal? Lte { get; init; }

    [Setting("`true` holds when the field is present and not null; `false` when it is missing or null.", Example = "true")]
    public bool? Exists { get; init; }

    [Setting("Holds when every one of these conditions holds.", Example = """[{ "field": "args.force", "is": "true" }]""")]
    public IReadOnlyList<Condition>? All { get; init; }

    [Setting("Holds when at least one of these conditions holds.", Example = """[{ "field": "args.force", "is": "true" }]""")]
    public IReadOnlyList<Condition>? Any { get; init; }

    [Setting("Holds when this condition does not.", Example = """{ "field": "args.draft", "is": "true" }""")]
    public Condition? Not { get; init; }

    /// <summary>
    /// Whether the condition holds for the value its fields' root names: a tool's arguments or a step's output. A missing
    /// field makes every test but <c>exists</c> false.
    /// </summary>
    public bool Holds(JsonElement root)
    {
        if (All is not null)
        {
            return All.All(condition => condition.Holds(root));
        }

        if (Any is not null)
        {
            return Any.Any(condition => condition.Holds(root));
        }

        if (Not is not null)
        {
            return !Not.Holds(root);
        }

        var value = Read(root, Field!);
        var present = value is { ValueKind: not JsonValueKind.Null };
        if (Exists is { } exists)
        {
            return present == exists;
        }

        if (!present)
        {
            return false;
        }

        if (Is is not null || In is not null)
        {
            return (In ?? [Is!]).Any(text => Matches(value!.Value, text));
        }

        return value!.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDecimal(out var number)
            && (Gt is null || number > Gt) && (Gte is null || number >= Gte) && (Lt is null || number < Lt) && (Lte is null || number <= Lte);
    }

    /// <summary>
    /// The problems of the condition against the JSON Schema of the value it reads: a malformed condition, a field the
    /// schema does not declare, or a test that can never hold (CFG-13).
    /// </summary>
    /// <param name="schema">The schema of the value.</param>
    /// <param name="root">The value's root in field paths: <c>args</c> for a tool's arguments, <c>output</c> for a step's output.</param>
    public IEnumerable<string> Check(JsonElement schema, string root = "args")
    {
        // The numeric comparisons may be combined into one range test, such as gte with lt; Holds requires all of them.
        var tests = new object?[] { Is, In, Gt ?? Gte ?? Lt ?? Lte, Exists }.Count(test => test is not null);
        var groups = new object?[] { All, Any, Not }.Count(group => group is not null);
        if (Field is null ? tests != 0 || groups != 1 : tests != 1 || groups != 0)
        {
            return ["needs either a field and exactly one test, or exactly one of all, any and not."];
        }

        if (Field is null)
        {
            return (All ?? Any ?? [Not!]).SelectMany(condition => condition.Check(schema, root));
        }

        var value = root == "args" ? "the tool's arguments" : "the step's output";
        if (!IsPath(Field, root))
        {
            return [$"field {Field} is not a path into {value}, such as {root}.{(root == "args" ? "branch" : "kind")}."];
        }

        var field = Walk(schema, Field, (node, name) => Child(node, "properties") is { } properties ? Child(properties, name) : null,
            (node, _) => Child(node, "items"));
        if (field is not { } declared)
        {
            return [$"field {Field} is not in {value}."];
        }

        var types = Child(declared, "type") switch
        {
            { ValueKind: JsonValueKind.String } type => [type.GetString()!],
            { ValueKind: JsonValueKind.Array } list => list.EnumerateArray().Select(type => type.GetString()!).ToArray(),
            _ => Array.Empty<string>(),
        };
        var problems = new List<string>();
        if ((Gt ?? Gte ?? Lt ?? Lte) is not null && !types.Any(type => type is "number" or "integer"))
        {
            problems.Add($"field {Field} is not a number, so gt, gte, lt and lte can never hold.");
        }

        problems.AddRange((In ?? (Is is null ? [] : [Is])).Where(text => !CanBe(declared, types, text)).Select(text => $"field {Field} can never be \"{text}\"."));
        return problems;
    }

    /// <summary>Whether a field path starts with the root and goes on with names and indexes, such as <c>output.files[0]</c>.</summary>
    internal static bool IsPath(string path, string root) => path.StartsWith(root, StringComparison.Ordinal) && FieldPath().IsMatch(path[root.Length..]);

    /// <summary>The value at a field path in the value its root names; null when there is none.</summary>
    internal static JsonElement? Read(JsonElement root, string path) =>
        Walk(root, path, (node, name) => Child(node, name), (node, index) => node.ValueKind == JsonValueKind.Array && index < node.GetArrayLength() ? node[index] : null);

    /// <summary>A value as text: a string as it is, anything else as JSON.</summary>
    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    private static bool CanBe(JsonElement schema, string[] types, string text) =>
        Child(schema, "enum") is { ValueKind: JsonValueKind.Array } choices
            ? choices.EnumerateArray().Any(choice => Matches(choice, text))
            : types.Length == 0 || types.Any(type => type switch
            {
                "string" => true,
                "number" or "integer" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                "boolean" => bool.TryParse(text, out _),
                _ => false,
            });

    /// <summary>Whether a JSON value equals a value written as text: numbers by value, booleans as <c>true</c> or <c>false</c>.</summary>
    private static bool Matches(JsonElement value, string text) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() == text,
        JsonValueKind.Number => value.TryGetDecimal(out var number)
            && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected) && number == expected,
        JsonValueKind.True or JsonValueKind.False => bool.TryParse(text, out var flag) && flag == (value.ValueKind == JsonValueKind.True),
        _ => false,
    };

    /// <summary>Follows a path after its root, one name or index at a time; null once a step finds nothing.</summary>
    private static JsonElement? Walk(JsonElement root, string path, Func<JsonElement, string, JsonElement?> name, Func<JsonElement, int, JsonElement?> index)
    {
        JsonElement? node = root;
        foreach (Match step in PathStep().Matches(path[path.IndexOfAny(['.', '['])..]))
        {
            node = node is not { } current ? null
                : step.Groups["name"].Success ? name(current, step.Groups["name"].Value)
                : index(current, int.Parse(step.Groups["index"].Value, CultureInfo.InvariantCulture));
        }

        return node;
    }

    private static JsonElement? Child(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var child) ? child : null;

    [GeneratedRegex(@"^(\.[A-Za-z0-9_-]+|\[[0-9]{1,9}\])+$")]
    private static partial Regex FieldPath();

    [GeneratedRegex(@"\.(?<name>[A-Za-z0-9_-]+)|\[(?<index>[0-9]+)\]")]
    private static partial Regex PathStep();
}
