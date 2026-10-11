namespace Sleepyshark.Officina;

/// <summary>Why a model stopped, in the model contract's words.</summary>
public enum ModelStopReason
{
    /// <summary>A reason the provider gave that is none of the others.</summary>
    Unknown,
    End,
    ToolUse,
    MaxTokens,
    Refusal,
    ContextFull,
}
