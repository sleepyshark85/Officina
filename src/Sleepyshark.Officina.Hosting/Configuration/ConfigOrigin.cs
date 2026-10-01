using System.Globalization;
using Sleepyshark.Officina.Core;

namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>The configuration layers, lowest to highest (configuration reference §13).</summary>
public enum LayerKind
{
    CodeDefault,
    Preset,
    ExtendedFile,
    ApplicationFile,
    EnvironmentFile,
    EnvironmentVariable,
    RunOption,
}

/// <summary>Where a value came from: its layer, and the file position, variable or option that set it (CFG-04).</summary>
public sealed record ConfigOrigin
{
    public ConfigOrigin(LayerKind layer, string? source = null, int line = 0, int column = 0)
    {
        Layer = layer;
        Source = source;
        Line = line;
        Column = column;
    }

    public static ConfigOrigin CodeDefault { get; } = new(LayerKind.CodeDefault);

    public LayerKind Layer { get; }

    /// <summary>The file (relative to the configuration directory), preset, environment variable or run option.</summary>
    public string? Source { get; }

    /// <summary>The 1-based line in the file, or 0.</summary>
    public int Line { get; }

    /// <summary>The 1-based column in the file, or 0.</summary>
    public int Column { get; }

    /// <summary>The full path of the file, for resolving <c>{ "file": … }</c> includes relative to it.</summary>
    public string? FullPath { get; init; }

    /// <summary>The definition a value was inherited from through <c>extends</c>, such as <c>agents.base-coder</c>, or null.</summary>
    public string? Via { get; init; }

    /// <summary>Where the value was written, such as <c>sof.json:4:7</c> or <c>SOF__run__permissionMode</c>; null for code defaults.</summary>
    public string? Location => Layer switch
    {
        LayerKind.CodeDefault => null,
        _ when Line > 0 => string.Create(CultureInfo.InvariantCulture, $"{Source}:{Line}:{Column}"),
        _ => Source,
    };

    public override string ToString() => Via is null ? Describe() : $"{Describe()}, through extends from {Via}";

    private string Describe() => Layer switch
    {
        LayerKind.CodeDefault => $"code default, core {CoreVersion.Value}",
        LayerKind.Preset => $"preset {Location}",
        LayerKind.ExtendedFile => $"extended file {Location}",
        LayerKind.ApplicationFile => $"application file {Location}",
        LayerKind.EnvironmentFile => $"environment file {Location}",
        LayerKind.EnvironmentVariable => $"environment variable {Location}",
        _ => $"run option {Location}",
    };
}
