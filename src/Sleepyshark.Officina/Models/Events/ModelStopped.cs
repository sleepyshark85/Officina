namespace Sleepyshark.Officina;

/// <summary>Why the model stopped: the reply's last event.</summary>
/// <param name="Reason">The neutral reason.</param>
/// <param name="Detail">The refusal's category, or the provider's own word for an unknown reason.</param>
public sealed record ModelStopped(ModelStopReason Reason, string? Detail = null) : ModelEvent;
