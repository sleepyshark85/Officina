using System.ComponentModel.DataAnnotations;

namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>What is stored, and for how long (EVT-05, PRIV-01). The host chooses the storage itself in code (STO-01).</summary>
public sealed record StorageOptions
{
    [Setting("Kinds of event that are published live but not stored, so a reader that joins late or falls behind does not see them. Unset leaves out streamed text only: storing each piece slows the agent, and the conversation keeps the text. An empty list in a file counts as unset; to store every kind, set an empty list in code.",
        Example = """["textGenerated", "modelCallEnded"]""")]
    public IReadOnlyList<string>? UnstoredEvents { get; init; }

    /// <summary>The kinds not stored: the configured ones, or streamed text when none are configured.</summary>
    internal IReadOnlyList<string> Unstored => UnstoredEvents ?? ["textGenerated"];

    [Setting("How long each kind of stored data is kept. Data without a period is kept until it is deleted.",
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

    [Setting("How long a turn of a stored conversation is kept after it ends, as `d.hh:mm:ss`. Once its earliest turns are deleted, a conversation continues from the turns kept.",
        Example = "\"90.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? Conversations { get; init; }
    [Setting("How long a run's record is kept after its last change, as `d.hh:mm:ss`. It is kept or deleted whole, never trimmed.",
        Example = "\"365.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? RunRecords { get; init; }

    [Setting("How long a run's task board, with each task's history, is kept after its last change, as `d.hh:mm:ss`. It is kept or deleted whole.",
        Example = "\"365.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? TaskBoards { get; init; }

    [Setting("How long an artifact, such as the full text of a trimmed tool result, is kept after it is made, as `d.hh:mm:ss`.",
        Example = "\"90.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = Positive)]
    public TimeSpan? Artifacts { get; init; }

    // PRIV-02: audit entries always have a period, so a deleted owner's entries are never kept forever.
    [Setting("How long an audit entry is kept after it is written, as `d.hh:mm:ss`. A request to delete an owner's data leaves audit entries to this period.",
        Example = "\"730.00:00:00\"")]
    [Range(typeof(TimeSpan), "00:00:00", "10675199.02:48:05.4775807", MinimumIsExclusive = true, ErrorMessage = "must be greater than zero.")]
    public TimeSpan Audit { get; init; } = TimeSpan.FromDays(365);
}
