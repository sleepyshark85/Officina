using Anthropic.Exceptions;
using Anthropic.Models;

namespace Sleepyshark.Officina.Claude;

/// <summary>What kind of failure a Claude call ended with, once retries are over or when retrying cannot help.</summary>
public enum ClaudeFailure
{
    /// <summary>Rate limits, overload, server errors and network errors, still failing after every attempt.</summary>
    Transient,

    /// <summary>The API key is missing, wrong, or not allowed to do this.</summary>
    Authentication,

    /// <summary>The API rejected the request as it is; sending it again cannot help.</summary>
    InvalidRequest,
}

/// <summary>A Claude call that failed, with its kind; the run ends as failed with its message.</summary>
public sealed class ClaudeException(ClaudeFailure failure, string message, Exception inner) : Exception(message, inner)
{
    public ClaudeFailure Failure { get; } = failure;
}

/// <summary>
/// Classifies the SDK's failures (S00b recommendation 5): by exception type first, then by error type, which is all an
/// error that arrives mid-stream has.
/// </summary>
internal static class ClaudeErrors
{
    /// <summary>Attempts per call, the first included.</summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Whether the failure may pass if the call is made again; the caller's own cancellation never is.</summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        _ when cancellationToken.IsCancellationRequested => false,
        AnthropicRateLimitException or Anthropic5xxException or AnthropicIOException or HttpRequestException or IOException => true,
        AnthropicSseException sse => sse.ErrorType is ErrorType.OverloadedError or ErrorType.ApiError or ErrorType.RateLimitError or ErrorType.TimeoutError,
        OperationCanceledException => true, // a timeout of the SDK or the HTTP client, not the caller's cancellation
        _ => false,
    };

    /// <summary>
    /// A prompt longer than the context window. The API gives it no type of its own, only an invalid-request error whose
    /// message says so; this is the one place the adapter reads an error's text.
    /// </summary>
    public static bool IsPromptTooLong(Exception exception) =>
        exception is AnthropicBadRequestException bad && bad.Message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase);

    /// <summary>The failure to throw, or null for one that is not the API's (such as the caller's cancellation), to let pass.</summary>
    public static ClaudeException? Classify(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is ClaudeException || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var failure = exception switch
        {
            _ when IsTransient(exception, cancellationToken) => ClaudeFailure.Transient,
            AnthropicUnauthorizedException or AnthropicForbiddenException => ClaudeFailure.Authentication,
            AnthropicSseException { ErrorType: ErrorType.AuthenticationError or ErrorType.PermissionError } => ClaudeFailure.Authentication,
            AnthropicException => ClaudeFailure.InvalidRequest,
            _ => (ClaudeFailure?)null,
        };
        return failure is { } known ? new ClaudeException(known, $"Claude call failed ({known}): {exception.Message}", exception) : null;
    }

    /// <summary>The wait before attempt <paramref name="attempt"/> + 1: what the API asked for, or an exponential backoff with jitter.</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan? asked)
    {
        if (asked is { } wait && wait > TimeSpan.Zero)
        {
            return wait;
        }

        var exponential = Math.Min(LongestBackoff.TotalMilliseconds, FirstBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(exponential * (0.5 + (Random.Shared.NextDouble() / 2)));
    }
}
