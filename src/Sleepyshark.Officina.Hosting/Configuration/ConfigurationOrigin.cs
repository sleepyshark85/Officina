using Sleepyshark.Officina.Core;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>Where a value came from: its layer, and the file, environment variable or run option that set it (CFG-04).</summary>
/// <param name="Layer">The layer.</param>
/// <param name="Source">The file (relative to the configuration directory), environment variable or run option.</param>
public sealed record ConfigurationOrigin(LayerKind Layer, string? Source = null)
{
    public static ConfigurationOrigin CodeDefault { get; } = new(LayerKind.CodeDefault);

    /// <summary>The definition a value was inherited from through <c>extends</c>, such as <c>agents.base</c>.</summary>
    public string? Via { get; init; }

    /// <summary>Where the value was written, such as <c>sof.json</c>; null for code defaults.</summary>
    public string? Location => Layer == LayerKind.CodeDefault ? null : Source;

    public override string ToString()
    {
        var layer = Layer switch
        {
            LayerKind.CodeDefault => $"code default, core {CoreVersion.Value}",
            LayerKind.ApplicationFile => $"application file {Source}",
            LayerKind.EnvironmentFile => $"environment file {Source}",
            LayerKind.EnvironmentVariable => $"environment variable {Source}",
            _ => $"run option {Source}",
        };
        return Via is null ? layer : $"{layer}, through extends from {Via}";
    }
}
