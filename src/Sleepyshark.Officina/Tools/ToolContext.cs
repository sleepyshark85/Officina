namespace Sleepyshark.Officina;

/// <summary>What a tool's handler knows of the run that calls it.</summary>
/// <param name="MemoryScope">Whose memory the run sees; null for a run without one.</param>
public sealed record ToolContext(string? MemoryScope);
