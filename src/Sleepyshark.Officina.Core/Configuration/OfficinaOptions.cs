namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The whole configuration. Files and the programmatic form both produce this object (CFG-02).
/// Every property's initial value is its default (CFG-16).
/// </summary>
[FileOnlySetting("$schema", FileOnlySettingKind.Text, "The JSON Schema of the file, for editor completion. Ignored when loading.",
    Example = "\"https://raw.githubusercontent.com/sleepyshark85/Officina/main/docs/officina.schema.json\"")]
[FileOnlySetting("extends", FileOnlySettingKind.TextList,
    "Presets (`preset:<id>`) and other files (paths relative to this one) this file builds on. Each is a lower layer; later entries override earlier ones.",
    Example = "[\"preset:coding-team\"]")]
public sealed record OfficinaOptions
{
    /// <summary>The only format version this core reads.</summary>
    public const int CurrentFormatVersion = 1;

    [Setting("The configuration format version. Unknown versions are rejected.", Example = "1", Minimum = CurrentFormatVersion, Maximum = CurrentFormatVersion)]
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    [Setting("The project's identity, and values usable in placeholders (CFG-14).", Example = "{ \"name\": \"invoice-api\" }")]
    public ProjectOptions Project { get; init; } = new();

    [Setting("Model providers, by name.", Example = "{ \"claude\": { \"type\": \"claude\", \"apiKey\": { \"secret\": \"ANTHROPIC_API_KEY\" } } }")]
    public NamedMap<ProviderOptions> Providers { get; init; } = NamedMap.Of((ProviderOptions.ClaudeName, ProviderOptions.Claude));

    [Setting("Model profiles, by name. Agents refer to them by name (MDL-02, MDL-03).", Example = "{ \"strong\": { \"model\": \"claude-opus-5-5\", \"effort\": \"high\" } }")]
    public NamedMap<ModelProfile> Models { get; init; } = NamedMap.Of((ModelProfile.DefaultName, new ModelProfile()));

    [Setting("Agent definitions, by name (CFG-01).", Example = "{ \"extractor\": { \"instructions\": \"Extract the invoice number.\" } }")]
    public NamedMap<AgentDefinition> Agents { get; init; } = NamedMap<AgentDefinition>.Empty;

    [Setting("Defaults for every run.", Example = "{ \"permissionMode\": \"ask\" }")]
    public RunDefaults Run { get; init; } = new();

    [Setting("Optional capabilities, by name. All are off by default (CAP-01).", Example = "{ \"conversationStore\": true }")]
    public NamedMap<CapabilitySettings> Capabilities { get; init; } = NamedMap<CapabilitySettings>.Empty;

    /// <summary>Whether the named capability is on.</summary>
    public bool IsEnabled(string capability) => Capabilities.TryGetValue(capability, out var settings) && settings.Enabled;
}
