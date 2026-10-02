using System.Net;

namespace Sleepyshark.Officina.Providers.Claude;

/// <summary>
/// Passes requests on to the HTTP client, and notes the wait that a failed response asks for in its <c>Retry-After</c>
/// header (REL-01), which the SDK's exceptions do not carry. The header is about the account, not one call, so the latest
/// one seen is the one to respect; it is read once, by the failure that follows.
/// </summary>
internal sealed class RetryAfterHandler : DelegatingHandler
{
    private long ticks = -1;

    public RetryAfterHandler(HttpMessageHandler inner)
        : base(inner)
    {
    }

    /// <summary>The wait the latest failed response asked for, once; null when it did not ask.</summary>
    public TimeSpan? Take() => Interlocked.Exchange(ref ticks, -1) is >= 0 and var taken ? TimeSpan.FromTicks(taken) : null;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError
            && response.Headers.RetryAfter is { } asked
            && (asked.Delta ?? asked.Date - DateTimeOffset.UtcNow) is { } wait)
        {
            Interlocked.Exchange(ref ticks, Math.Max(0, wait.Ticks));
        }

        return response;
    }
}
