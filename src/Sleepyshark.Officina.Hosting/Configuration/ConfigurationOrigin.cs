using System.Globalization;
using Sleepyshark.Officina.Core;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>Where a value came from: its layer, and the file position, variable or option that set it (CFG-04).</summary>
/// <param name="Layer">The layer.</param>
/// <param name="Source">The file (relative to the configuration directory), environment variable or run option.</param>
/// <param name="Line">The 1-based line in the file, or 0.</param>
/// <param name="Column">The 1-based column in the file, or 0.</param>
public sealed record ConfigurationOrigin(LayerKind Layer, string? Source = null, int Line = 0, int Column = 0)
{
    public static ConfigurationOrigin CodeDefault { get; } = new(LayerKind.CodeDefault);

    /// <summary>The definition a value was inherited from through <c>extends</c>, such as <c>agents.base</c>.</summary>
    public string? Via { get; init; }

    /// <summary>Where the value was written, such as <c>sof.json:4:7</c>; null for code defaults.</summary>
    public string? Location => Layer switch
    {
        LayerKind.CodeDefault => null,
        _ when Line > 0 => string.Create(CultureInfo.InvariantCulture, $"{Source}:{Line}:{Column}"),
        _ => Source,
    };

    public override string ToString()
    {
        var layer = Layer switch
        {
            LayerKind.CodeDefault => $"code default, core {CoreVersion.Value}",
            LayerKind.ExtendedFile => $"extended file {Location}",
            LayerKind.ApplicationFile => $"application file {Location}",
            LayerKind.EnvironmentFile => $"environment file {Location}",
            LayerKind.EnvironmentVariable => $"environment variable {Location}",
            _ => $"run option {Location}",
        };
        return Via is null ? layer : $"{layer}, through extends from {Via}";
    }
}
