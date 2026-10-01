namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The project's identity, and values that instructions can use as placeholders (CFG-14).</summary>
public sealed record ProjectOptions
{
    [Setting("The project's name. Usable in instructions as `{{project.name}}`.", Example = "\"invoice-api\"")]
    public string? Name { get; init; }

    [Setting("Free-form text values. Usable in instructions as `{{project.values.<name>}}`.", Example = """{ "testCommand": "dotnet test" }""")]
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
}
