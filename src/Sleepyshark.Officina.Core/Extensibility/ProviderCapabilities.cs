using Sleepyshark.Officina.Core.Configuration;

namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// What a provider supports, checked when configuration is validated (MDL-02, MDL-06). A list that is
/// null is not declared, so profiles are not checked against it. Capabilities are added as the features
/// that need them arrive.
/// </summary>
public sealed record ProviderCapabilities
{
    /// <summary>A provider that declares nothing, so its profiles are not checked.</summary>
    public static ProviderCapabilities None { get; } = new();

    /// <summary>The reasoning efforts the provider accepts.</summary>
    public ValueList<string>? Efforts { get; init; }

    /// <summary>The names of the provider-declared settings a profile may set under <c>settings</c>.</summary>
    public ValueList<string>? Settings { get; init; }

    /// <summary>The tool-choice modes the provider accepts.</summary>
    public ValueList<string> ToolChoices { get; init; } = ["auto", "none"];

    /// <summary>The largest <c>maxOutputTokens</c> the provider accepts, or null when it does not say.</summary>
    public int? MaxOutputTokens { get; init; }
}
