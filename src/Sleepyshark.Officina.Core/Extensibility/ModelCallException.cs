namespace Sleepyshark.Officina.Core.Extensibility;

/// <summary>
/// A failed model call, classified (MDL-05). A provider throws it from <see cref="IModelProvider.StreamAsync"/>; an input
/// too long for the model is not a failure but the stop reason <c>InputTooLong</c>, so the history can be shortened.
/// </summary>
/// <remarks>Its message names the failure only, never the request, which may hold secrets or personal data.</remarks>
public sealed class ModelCallException : Exception
{
    public ModelCallException()
    {
    }

    public ModelCallException(string message)
        : base(message)
    {
    }

    public ModelCallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <param name="failure">What kind of failure it is.</param>
    /// <param name="innerException">The provider's own exception.</param>
    public ModelCallException(ModelFailure failure, Exception? innerException = null)
        : base($"The model call failed: {failure}.", innerException) => Failure = failure;

    public ModelFailure Failure { get; }
}

public enum ModelFailure
{
    /// <summary>The provider is overloaded or unreachable, or failed on its side; trying again may succeed.</summary>
    Transient,

    /// <summary>The account's rate limit is reached; trying again later may succeed.</summary>
    RateLimited,

    /// <summary>The provider refused the request as it is.</summary>
    InvalidRequest,

    /// <summary>The credential is missing, wrong or not allowed to make the call.</summary>
    Authentication,
}
