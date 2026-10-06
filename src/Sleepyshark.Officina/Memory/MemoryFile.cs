namespace Sleepyshark.Officina;

/// <summary>A memory file: its path within the scope (parts separated by <c>/</c>) and its size in bytes.</summary>
public sealed record MemoryFile(string Path, long Size);
