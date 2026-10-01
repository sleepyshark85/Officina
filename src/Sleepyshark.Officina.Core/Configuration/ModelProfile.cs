using System.Text.Json;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>Which provider and model a model slot uses, and how (MDL-02).</summary>
public sealed record ModelProfile
{
    /// <summary>The name of the profile an agent uses when it names none (CFG-03).</summary>
    public const string DefaultName = "default";

    [Setting("The provider that serves this profile, by its name in `providers`.", Example = "\"claude\"")]
    public string Provider { get; init; } = ProviderOptions.ClaudeName;

    [Setting("The provider's model id.", Example = "\"claude-opus-5-5\"")]
    public string Model { get; init; } = "claude-opus-5-5";

    [Setting("Reasoning effort, such as `low`, `medium` or `high`. Unset uses the provider's default.", Example = "\"high\"")]
    public string? Effort { get; init; }

    [Setting("The most tokens one reply may have. Unset uses the provider's default.", Example = "64000", Minimum = 1)]
    public int? MaxOutputTokens { get; init; }

    [Setting("Whether the model may call tools.", Example = "\"auto\"")]
    public ToolChoice ToolChoice { get; init; } = ToolChoice.Auto;

    [Setting("Any other setting the provider declares for the model, such as a temperature.", Example = """{ "temperature": 0.2 }""")]
    public IReadOnlyDictionary<string, JsonElement> Settings { get; init; } = new Dictionary<string, JsonElement>();
}
