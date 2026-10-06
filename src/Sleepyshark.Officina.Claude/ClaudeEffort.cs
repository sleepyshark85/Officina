namespace Sleepyshark.Officina.Claude;

/// <summary>How hard Claude thinks and how much it spends; always set, never left to the model's default.</summary>
public enum ClaudeEffort
{
    Low,
    Medium,
    High,
    XHigh,
    Max,
}
