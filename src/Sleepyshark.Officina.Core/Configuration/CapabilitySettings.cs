namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>
/// The settings of one capability. Each capability derives its own record from this one. In files,
/// <c>true</c> and <c>false</c> are shorthand for <c>{ "enabled": … }</c>.
/// </summary>
public abstract record CapabilitySettings
{
    [Setting("Whether the capability is on. Every capability is off by default (CAP-01).", Example = "true")]
    public bool Enabled { get; init; }
}
