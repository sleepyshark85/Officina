namespace Sleepyshark.Officina;

/// <summary>A change to a tool source's connection; <paramref name="Detail"/> says why it failed or was lost.</summary>
public sealed record ToolSourceChange(ToolSourceState State, string? Detail = null);
