namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>How tool calls that need permission are decided (HITL-01).</summary>
public enum PermissionMode
{
    Ask,
    Auto,
    ReadOnly,
}
