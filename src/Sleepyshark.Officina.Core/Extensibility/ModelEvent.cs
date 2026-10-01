namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Something a provider streams back during a model call. S04 adds reasoning, tool requests, usage and stop reasons.</summary>
public abstract record ModelEvent;

/// <summary>Generated text. The reply's text is the concatenation of its deltas.</summary>
public sealed record TextDelta(string Text) : ModelEvent;
