namespace Sleepyshark.Officina.Testing;

/// <summary>One measurement: the instrument's name, the value and its tags.</summary>
public sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
