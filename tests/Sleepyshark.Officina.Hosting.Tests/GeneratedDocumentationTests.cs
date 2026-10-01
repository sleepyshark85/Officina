using System.Text.Json;
using System.Text.RegularExpressions;
using Json.Schema;
using Sleepyshark.Officina.Hosting.Documentation;

namespace Sleepyshark.Officina.Hosting.Tests;

/// <summary>
/// The JSON Schema and the settings reference are generated from the Options classes, and the committed copies
/// must be current (CFG-15, DOC-01). To regenerate them, run the tests with OFFICINA_UPDATE_GENERATED=1.
/// </summary>
public sealed partial class GeneratedDocumentationTests
{
    private const string UpdateVariable = "OFFICINA_UPDATE_GENERATED";

    private static readonly string Root = FindRoot();
    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(SchemaGenerator.GenerateText()));

    [Fact]
    public void The_committed_JSON_Schema_is_generated_from_the_Options_classes() =>
        AssertCurrent("docs/officina.schema.json", SchemaGenerator.GenerateText());

    [Fact]
    public void The_committed_settings_reference_is_generated_from_the_schema() =>
        AssertCurrent("docs/configuration-settings.md", SettingsReferenceGenerator.Generate(SchemaGenerator.Generate()));

    [Fact]
    public void The_examples_in_the_configuration_guide_validate_against_the_schema()
    {
        var guide = File.ReadAllText(Path.Combine(Root, "CONFIGURATION.md")).ReplaceLineEndings("\n");
        var examples = JsonBlock().Matches(guide).Select(match => match.Groups["json"].Value).ToArray();

        Assert.NotEmpty(examples);
        Assert.All(examples, AssertValid);
    }

    [Fact]
    public void The_schema_accepts_a_partial_layer_and_rejects_unknown_settings()
    {
        AssertValid("""{ "agents": { "a": { "model": "strong" } }, "project": { "name": null }, "extends": ["base.json"] }""");
        Assert.False(Evaluate("""{ "agents": { "a": { "instruction": "x" } } }""").IsValid);
        Assert.False(Evaluate("""{ "providers": { "claude": { "apiKey": "sk-ant-literal" } } }""").IsValid);
        Assert.False(Evaluate("""{ "run": { "budget": { "cost": null } } }""").IsValid);
        Assert.False(Evaluate("""{ "run": { "budget": { "time": "8 hours" } } }""").IsValid);
    }

    private static void AssertCurrent(string relativePath, string generated)
    {
        var path = Path.Combine(Root, relativePath);
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }

        var committed = File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : "";
        var regenerate = $"{UpdateVariable}=1 dotnet test --filter {nameof(GeneratedDocumentationTests)}";
        Assert.True(committed == generated, $"{relativePath} is out of date with the Options classes. Regenerate it: {regenerate}");
    }

    private static void AssertValid(string json)
    {
        var result = Evaluate(json);
        var problems = (result.Details ?? [])
            .Where(detail => detail.Errors is not null)
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"));
        Assert.True(result.IsValid, $"{json}\n" + string.Join('\n', problems));
    }

    private static EvaluationResults Evaluate(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Schema.Value.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sleepyshark.Officina.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("```json\\n(?<json>.*?)```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();
}
