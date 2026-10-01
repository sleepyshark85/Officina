namespace Sleepyshark.Officina.Core.Configuration;

/// <summary>The validation phases that apply so far, numbered as in configuration reference §14.</summary>
public enum ValidationPhase
{
    Parse = 1,
    Shape = 2,
    Merge = 3,
    References = 4,
    Tools = 7,
    Conditions = 8,
    Prefix = 9,
    Invariants = 10,
}
