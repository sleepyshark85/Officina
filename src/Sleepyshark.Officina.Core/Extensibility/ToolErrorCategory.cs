namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>Why a tool call did not succeed, as the model is told (TOOL-08).</summary>
public enum ToolErrorCategory
{
    UnknownTool,
    InvalidArguments,
    NotAuthorised,
    PolicyViolation,
    Timeout,
    Unavailable,
    Failed,
}
