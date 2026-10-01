namespace Sleepyshark.Officina.Workspace;

/// <summary>The workspace refused an operation. The message says why and what to do, and is safe to show the agent or the owner.</summary>
public sealed class WorkspaceException(string message) : Exception(message);
