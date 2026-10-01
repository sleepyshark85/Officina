namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The whole configuration. Files and the programmatic form both produce this object (CFG-02), and every property's
/// initial value is its default (CFG-16). Records with collections compare those by reference, so two equal-looking
/// configurations are not <c>Equals</c>; compare their serialized form instead.
/// </summary>
public sealed partial record OfficinaOptions
{
    /// <summary>The only format version this core reads.</summary>
    public const int CurrentFormatVersion = 1;

    [Setting("The configuration format version. Unknown versions are rejected.", Example = "1")]
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    [Setting("The project's identity, and values usable in placeholders.", Example = """{ "name": "invoice-api" }""")]
    public ProjectOptions Project { get; init; } = new();

    [Setting("Model providers, by name.", Example = """{ "claude": { "apiKey": { "secret": "ANTHROPIC_API_KEY" } } }""")]
    public IReadOnlyDictionary<string, ProviderOptions> Providers { get; init; } =
        new Dictionary<string, ProviderOptions> { [ProviderOptions.ClaudeName] = ProviderOptions.Claude };

    [Setting("Model profiles, by name. Agents refer to them by name.", Example = """{ "strong": { "effort": "high" } }""")]
    public IReadOnlyDictionary<string, ModelProfile> Models { get; init; } =
        new Dictionary<string, ModelProfile> { [ModelProfile.DefaultName] = new() };

    [Setting("Agent definitions, by name.", Example = """{ "extractor": { "instructions": "Extract the invoice number." } }""")]
    public IReadOnlyDictionary<string, AgentDefinition> Agents { get; init; } = new Dictionary<string, AgentDefinition>();

    [Setting("Tools, by the name the model sees.", Example = """{ "create_issue": { "source": "extension:Acme.CreateIssue", "gates": ["issue-dedupe"] } }""")]
    public IReadOnlyDictionary<string, ToolOptions> Tools { get; init; } = new Dictionary<string, ToolOptions>();

    [Setting("Named groups of tools, by name in `tools`. Agents are offered tools by tool set.", Example = """{ "issues": ["create_issue", "find_issue"] }""")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ToolSets { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    [Setting("Gates, by name. Tools and policies refer to them by name.", Example = """{ "issue-dedupe": { "use": "extension:Acme.IssueDedupeGate" } }""")]
    public IReadOnlyDictionary<string, GateOptions> Gates { get; init; } = new Dictionary<string, GateOptions>();

    [Setting("Permission rules, gates for all tools, and anonymous callers' permissions.", Example = """{ "gates": ["no-main-branch"] }""")]
    public PolicyOptions Policies { get; init; } = new();

    [Setting("Defaults for every run.", Example = """{ "permissionMode": "ask" }""")]
    public RunDefaults Run { get; init; } = new();
}
