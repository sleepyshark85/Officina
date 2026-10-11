namespace Sleepyshark.Officina;

/// <summary>A piece of reply text, for display as it streams; the complete block follows in a <see cref="BlockReceived"/>.</summary>
public sealed record TextDelta(string Text) : ModelEvent;
