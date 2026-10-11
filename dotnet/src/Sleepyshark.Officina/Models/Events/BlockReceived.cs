namespace Sleepyshark.Officina;

/// <summary>A complete content block of the reply, in reply order.</summary>
public sealed record BlockReceived(ContentBlock Block) : ModelEvent;
