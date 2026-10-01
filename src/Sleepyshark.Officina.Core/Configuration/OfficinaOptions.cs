namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The whole configuration. Files and the programmatic form both produce this object (CFG-02).
/// Every property's initial value is its default (CFG-16).
/// </summary>
public sealed record OfficinaOptions
{
    /// <summary>The only format version this core reads.</summary>
    public const int CurrentFormatVersion = 1;

    [Setting("The configuration format version. Unknown versions are rejected.", Example = "1")]
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    [Setting("The project's identity, and values usable in placeholders (CFG-14).", Example = """{ "name": "invoice-api" }""")]
    public ProjectOptions Project { get; init; } = new();

    [Setting("Model providers, by name.", Example = """{ "claude": { "apiKey": { "secret": "ANTHROPIC_API_KEY" } } }""")]
    public IReadOnlyDictionary<string, ProviderOptions> Providers { get; init; } =
        new Dictionary<string, ProviderOptions> { [ProviderOptions.ClaudeName] = ProviderOptions.Claude };

    [Setting("Model profiles, by name. Agents refer to them by name (MDL-02, MDL-03).", Example = """{ "strong": { "effort": "high" } }""")]
    public IReadOnlyDictionary<string, ModelProfile> Models { get; init; } =
        new Dictionary<string, ModelProfile> { [ModelProfile.DefaultName] = new() };

    [Setting("Agent definitions, by name (CFG-01).", Example = """{ "extractor": { "instructions": "Extract the invoice number." } }""")]
    public IReadOnlyDictionary<string, AgentDefinition> Agents { get; init; } = new Dictionary<string, AgentDefinition>();

    [Setting("Defaults for every run.", Example = """{ "permissionMode": "ask" }""")]
    public RunDefaults Run { get; init; } = new();
}
