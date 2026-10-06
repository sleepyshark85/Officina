using System.Net;
using Anthropic.Exceptions;
using Anthropic.Models;

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

/// <summary>A Claude call that failed, with its kind; the run fails with its message.</summary>
public sealed class ClaudeException(ClaudeFailure failure, string message, Exception inner) : Exception(message, inner)
{
    public ClaudeFailure Failure { get; } = failure;
}

/// <summary>Classifies the SDK's failures by exception type, then by error type, which is all a mid-stream error has.</summary>
internal static class ClaudeErrors
{
    /// <summary>Attempts per call, the first included.</summary>
    public const int MaxAttempts = 5;

    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan LongestBackoff = TimeSpan.FromSeconds(30);

    /// <summary>Whether the failure may pass on another attempt; the caller's own cancellation never does.</summary>
    public static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        _ when cancellationToken.IsCancellationRequested => false,
        AnthropicRateLimitException or Anthropic5xxException or AnthropicIOException or HttpRequestException or IOException => true,
        AnthropicApiException { StatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict } => true, // as the SDK retries by default
        AnthropicSseException sse => sse.ErrorType is ErrorType.OverloadedError or ErrorType.ApiError or ErrorType.RateLimitError or ErrorType.TimeoutError,
        OperationCanceledException => true, // a timeout of the SDK or the HTTP client, not the caller's cancellation
        _ => false,
    };

    /// <summary>
    /// A prompt longer than the context window. The API gives it no type, only an invalid-request error whose message says
    /// so; this is the one place the adapter reads an error's text.
    /// </summary>
    public static bool IsPromptTooLong(Exception exception) =>
        exception is AnthropicBadRequestException bad && bad.Message.Contains("prompt is too long", StringComparison.OrdinalIgnoreCase);

    /// <summary>The failure to throw, or null for one that is not the API's (such as the caller's cancellation).</summary>
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

    /// <summary>
    /// The wait before the next attempt: what the API asked for, capped so a server cannot hold a call for ever, or else an
    /// exponential backoff with jitter.
    /// </summary>
    public static TimeSpan Backoff(int attempt, TimeSpan? asked)
    {
        if (asked is { } wait && wait > TimeSpan.Zero)
        {
            return wait < LongestBackoff ? wait : LongestBackoff;
        }

        var exponential = Math.Min(LongestBackoff.TotalMilliseconds, FirstBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(exponential * (0.5 + (Random.Shared.NextDouble() / 2)));
    }
}
