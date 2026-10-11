using System.Net;
using System.Runtime.CompilerServices;

namespace Sleepyshark.Officina.Claude;

/// <summary>
/// Notes the wait a failed response asks for in its <c>Retry-After</c> header, which the SDK's exceptions do not carry.
/// It goes to the call that sent the request through an async-local box, so concurrent calls do not mix.
/// </summary>
internal sealed class RetryAfterHandler(HttpMessageHandler inner, TimeProvider time) : DelegatingHandler(inner)
{
    private static readonly AsyncLocal<StrongBox<TimeSpan?>?> Asked = new();

    /// <summary>Moves the stream on, noting in <paramref name="asked"/> any wait a failed response asks for.</summary>
    public static async ValueTask<bool> MoveNextAsync<T>(IAsyncEnumerator<T> stream, StrongBox<TimeSpan?> asked)
    {
        Asked.Value = asked;
        return await stream.MoveNextAsync().ConfigureAwait(false);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (Asked.Value is { } asked
            && response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError
            && response.Headers.RetryAfter is { } retryAfter)
        {
            asked.Value = retryAfter.Delta ?? retryAfter.Date - time.GetUtcNow();
        }

        return response;
    }
}
