using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>What is stored, and for how long (EVT-05, PRIV-01). The host chooses the storage itself in code (STO-01).</summary>
public sealed record StorageOptions
{
    [Setting("Kinds of event that are published live but not stored, so a reader that joins late or falls behind does not see them.",
        Example = """["textGenerated"]""")]
    public IReadOnlyList<string> UnstoredEvents { get; init; } = [];

    [Setting("How long each kind of stored data is kept. A kind without a period is kept until it is deleted.",
        Example = """{ "events": "30.00:00:00" }""")]
    public RetentionOptions Retention { get; init; } = new();
}

/// <summary>How long each kind of stored data is kept (PRIV-01). The slices that store other kinds add theirs.</summary>
public sealed record RetentionOptions
{
    private const string Positive = "must be greater than zero. Leave it unset to keep the data until it is deleted.";

    [Setting("How long a run, with the configuration it used, is kept after it starts, as `d.hh:mm:ss`.", Example = "\"90.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? Runs { get; init; }

    [Setting("How long an event is kept after it happens, as `d.hh:mm:ss`.", Example = "\"30.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? Events { get; init; }

    [Setting("How long an audit entry is kept after it is written, as `d.hh:mm:ss`. A request to delete an owner's data leaves audit entries to this period.",
        Example = "\"365.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? Audit { get; init; }
}
