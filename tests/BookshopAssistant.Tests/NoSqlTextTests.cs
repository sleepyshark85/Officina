using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace BookshopAssistant.Tests;

/// <summary>No tool takes SQL text; every query is a fixed, parameterized constant. Static checks, no database.</summary>
public partial class NoSqlTextTests
{
    /// <summary>The only text the model may give: values compared as values, never run.</summary>
    private static readonly string[] TextValues = ["author", "email", "genre", "name", "nameOrEmail", "title"];

    [Fact]
    public void The_only_text_inputs_are_known_values()
    {
        using var database = NpgsqlDataSource.Create("Host=unused");
        var texts = new BookshopTools(database).All
            .SelectMany(tool => StringProperties(JsonDocument.Parse(tool.InputSchema).RootElement))
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal(TextValues, texts);
    }

    [Fact]
    public void Every_command_runs_a_constant()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "apps", "BookshopAssistant", "Tools", "BookshopTools.cs"));
        var constants = ConstantSql().Matches(source).Select(match => match.Groups[1].Value).ToHashSet();
        var commands = Command().Matches(source).Select(match => match.Groups[1].Value).ToList();

        Assert.NotEmpty(commands);
        Assert.All(commands, command => Assert.Contains(command, constants));
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
    }

    private static IEnumerable<string> StringProperties(JsonElement schema)
    {
        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            var type = property.Value.GetProperty("type");
            if (type.ValueKind == JsonValueKind.String ? type.GetString() == "string" : type.EnumerateArray().Any(name => name.GetString() == "string"))
            {
                yield return property.Name;
            }
            else if (property.Value.TryGetProperty("items", out var items) && items.TryGetProperty("properties", out _))
            {
                foreach (var nested in StringProperties(items))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Sleepyshark.Officina.slnx")))
        {
            directory = directory.Parent ?? throw new InvalidOperationException("The repository root was not found.");
        }

        return directory.FullName;
    }

    [GeneratedRegex(@"private const string (\w+Sql) =")]
    private static partial Regex ConstantSql();

    /// <summary>Every way the tools make a command, with its first argument.</summary>
    [GeneratedRegex(@"(?:CreateCommand|new NpgsqlCommand)\(([^,)]*)")]
    private static partial Regex Command();
}
