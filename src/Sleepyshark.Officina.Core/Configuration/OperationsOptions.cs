using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>How the engine is operated.</summary>
public sealed record OperationsOptions
{
    [Setting("Measurements and the warnings raised from them.", Example = """{ "cacheHitWarning": 0.7 }""")]
    [Required(ErrorMessage = Messages.Required)]
    public TelemetryOptions Telemetry { get; init; } = new();

    // STO-01.
    [Setting("Where the default local storage keeps its files, for a host that uses it, such as `sof`. A host that supplies its own storage in code has no use for it.",
        Example = """{ "path": ".sof/sof.db" }""")]
    [Required(ErrorMessage = Messages.Required)]
    public LocalStorageOptions Storage { get; init; } = new();
}

/// <summary>Measurements and the warnings raised from them.</summary>
public sealed record TelemetryOptions
{
    // COST-01.
    [Setting("The share of a model call's input read from the provider's cache, from 0 to 1, below which a warning is raised. The first call of a turn is not checked.",
        Example = "0.7")]
    [Range(0d, 1d, ErrorMessage = "must be between 0 and 1.")]
    public double CacheHitWarning { get; init; } = 0.7;
}

/// <summary>
/// Where the default local storage keeps its files (STO-01): a SQLite database, and beside it the folder <c>artifacts</c>, with a
/// file for each artifact. The host that opens it resolves the path and checks where it leads.
/// </summary>
public sealed record LocalStorageOptions
{
    [Setting("The database file (SQLite); the artifacts' files are in the folder `artifacts` beside it. A relative path is relative to the project directory and must stay in `.sof/`, which agents cannot see. An absolute path must lead into the project's `.sof/` or out of the project.",
        Example = "\".sof/sof.db\"")]
    [Required(ErrorMessage = Messages.Required)]
    public string Path { get; init; } = $"{WorkspaceOptions.StateFolder}/sof.db";
}
