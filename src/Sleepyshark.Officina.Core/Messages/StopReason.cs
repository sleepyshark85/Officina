namespace Sleepyshark.Officina.Core.Messages;

/// <summary>Why the model stopped (MSG-05). The turn decides its next step from this alone (LOOP-03, INV-01).</summary>
public enum StopReason
{
    /// <summary>Anything the provider reports that is none of the others.</summary>
    Unknown,
    Finished,
    WantsTools,
    OutputLimit,
    StopSequence,

    /// <summary>The provider paused a long reply; calling again continues it.</summary>
    Paused,
    Refused,
    InputTooLong,
}
