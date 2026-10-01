namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The project's identity, and values that placeholders can use anywhere, including the stable prefix (CFG-14).</summary>
public sealed record ProjectOptions
{
    [Setting("The project's name. Usable as `{{project.name}}`.", Example = "\"invoice-api\"")]
    public string? Name { get; init; }

    [Setting("Free-form text values. Usable as `{{project.values.<name>}}`.", Example = "{ \"buildCommand\": \"dotnet build\", \"testCommand\": \"dotnet test\" }")]
    public NamedMap<string> Values { get; init; } = NamedMap<string>.Empty;
}
