namespace Sleepyshark.Officina;

/// <summary>
/// The call failed and is made again: everything streamed before this belongs to a reply that will not come. Usage the
/// failed attempt reported stays counted.
/// </summary>
public sealed record ModelRetried : ModelEvent;
