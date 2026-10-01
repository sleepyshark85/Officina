namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The project's identity, and values placeholders can use anywhere, including the stable prefix (CFG-14).</summary>
public sealed record ProjectOptions
{
    [Setting("The project's name. Usable as `{{project.name}}`.", Example = "\"invoice-api\"")]
    public string? Name { get; init; }

    [Setting("Free-form text values. Usable as `{{project.values.<name>}}`.", Example = """{ "testCommand": "dotnet test" }""")]
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}
