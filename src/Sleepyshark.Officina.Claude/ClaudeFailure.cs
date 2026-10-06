namespace Sleepyshark.Officina.Claude;

/// <summary>What kind of failure a Claude call ended with, once retries are over or cannot help.</summary>
public enum ClaudeFailure
{
    /// <summary>Rate limits, overload, server or network errors that persisted through every attempt.</summary>
    Transient,

    /// <summary>The API key is missing, wrong, or not allowed to do this.</summary>
    Authentication,

    /// <summary>The API rejected the request as it is; sending it again cannot help.</summary>
    InvalidRequest,
}
