namespace Sleepyshark.Officina.Hosting.Configuration;

/// <summary>The configuration layers, lowest to highest (configuration reference §13).</summary>
public enum LayerKind
{
    CodeDefault,
    ExtendedFile,
    ApplicationFile,
    EnvironmentFile,
    EnvironmentVariable,
    RunOption,
}
