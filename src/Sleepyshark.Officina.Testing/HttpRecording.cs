using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Real model exchanges, recorded and replayed offline (TEST-02). A recording is a JSON file: an array of exchanges, each
/// with the request body, the beta features it asked for (its <c>anthropic-beta</c> header) if any, the status, and the
/// response, which is the array of streamed events or the error body, and optionally the seconds of a <c>Retry-After</c>
/// header. No other header is kept, so the API key is never recorded.
/// </summary>
public static class HttpRecording
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Sends requests on and records each exchange to <paramref name="path"/>. The response is read whole before it is
    /// passed on, so it is recorded as it arrived.
    /// </summary>
    public static HttpMessageHandler Record(string path) => new Recorder(path) { InnerHandler = new HttpClientHandler() };

    /// <summary>
    /// Answers each request with the next recorded response. A request that differs from the recorded one fails with
    /// both, so a replay checks the requests as well as reading the responses; an exchange without a request answers any.
    /// </summary>
    public static HttpMessageHandler Replay(string path) => new Replayer(JsonNode.Parse(File.ReadAllText(path))!.AsArray());

    private sealed class Recorder(string path) : DelegatingHandler
    {
        private readonly JsonArray exchanges = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var events = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            var exchange = new JsonObject { ["request"] = JsonNode.Parse(body) };
            if (Beta(request) is { } beta)
            {
                exchange["beta"] = beta;
            }

            exchange["status"] = (int)response.StatusCode;
            exchange["response"] = events
                ? new JsonArray([.. text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[6..]))])
                : JsonNode.Parse(text);
            exchanges.Add(exchange);
            await File.WriteAllTextAsync(path, exchanges.ToJsonString(Indented), cancellationToken).ConfigureAwait(false);
            var replayed = Response(exchanges[^1]!);
            response.Dispose();
            return replayed;
        }
    }

    private sealed class Replayer(JsonArray exchanges) : HttpMessageHandler
    {
        private int next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var exchange = next < exchanges.Count ? exchanges[next++]! : throw new InvalidOperationException($"The recording has no exchange {next + 1}.");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (exchange["request"] is { } recorded && !JsonNode.DeepEquals(body, recorded))
            {
                throw new InvalidOperationException($"Request {next} differs from the recording.\nRecorded: {recorded.ToJsonString(Indented)}\nSent: {body!.ToJsonString(Indented)}");
            }

            // A recording made with beta features names them, and its replay checks that the same ones are asked for.
            if (exchange["beta"] is { } beta && beta.GetValue<string>() != Beta(request))
            {
                throw new InvalidOperationException($"Request {next} asks for beta features \"{Beta(request)}\", and the recording for \"{beta}\".");
            }

            return Response(exchange);
        }
    }

    /// <summary>The beta features a request asks for, in the order sent; null when none.</summary>
    private static string? Beta(HttpRequestMessage request) =>
        request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : null;

    private static HttpResponseMessage Response(JsonNode exchange)
    {
        var response = exchange["response"]!;
        var message = new HttpResponseMessage((HttpStatusCode)exchange["status"]!.GetValue<int>())
        {
            Content = response is JsonArray events
                ? new StringContent(
                    string.Concat(events.Select(data => $"event: {data!["type"]}\ndata: {data.ToJsonString()}\n\n")), Encoding.UTF8, "text/event-stream")
                : new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (exchange["retryAfter"] is { } seconds)
        {
            message.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds.GetValue<double>()));
        }

        return message;
    }
}
