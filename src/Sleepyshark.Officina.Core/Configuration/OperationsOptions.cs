using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>How the engine is operated. Later slices add storage, secrets and retention.</summary>
public sealed record OperationsOptions
{
    [Setting("Measurements and the warnings raised from them.", Example = """{ "cacheHitWarning": 0.7 }""")]
    [Required(ErrorMessage = Messages.Required)]
    public TelemetryOptions Telemetry { get; init; } = new();
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
