namespace Sleepyshark.Officina;

/// <summary>A piece of the model's reply text.</summary>
public sealed record TextStreamed(string Text) : RunEvent;
