using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Real model exchanges, recorded and replayed offline (TEST-02). A recording is a JSON file: an array of exchanges, each
/// with the request body, the status, and the response, which is the array of streamed events or the error body. Only
/// bodies are kept, never headers, so the API key is never recorded.
/// </summary>
public static class HttpRecording
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Sends requests on and records each exchange to <paramref name="path"/>. The response is read whole before it is
    /// passed on, so it is recorded as it arrived.
    /// </summary>
    public static HttpClient Record(string path) => new(new Recorder(path) { InnerHandler = new HttpClientHandler() }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Answers each request with the next recorded response. A request that differs from the recorded one fails with
    /// both, so a replay checks the requests as well as reading the responses; an exchange without a request answers any.
    /// </summary>
    public static HttpClient Replay(string path) => new(new Replayer(JsonNode.Parse(File.ReadAllText(path))!.AsArray()));

    private sealed class Recorder(string path) : DelegatingHandler
    {
        private readonly JsonArray exchanges = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var events = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
            exchanges.Add(new JsonObject
            {
                ["request"] = JsonNode.Parse(body),
                ["status"] = (int)response.StatusCode,
                ["response"] = events
                    ? new JsonArray([.. text.Split('\n').Where(line => line.StartsWith("data: ", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[6..]))])
                    : JsonNode.Parse(text),
            });
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
            return exchange["request"] is null || JsonNode.DeepEquals(body, exchange["request"])
                ? Response(exchange)
                : throw new InvalidOperationException(
                    $"Request {next} differs from the recording.\nRecorded: {exchange["request"]!.ToJsonString(Indented)}\nSent: {body!.ToJsonString(Indented)}");
        }
    }

    private static HttpResponseMessage Response(JsonNode exchange)
    {
        var response = exchange["response"]!;
        return new HttpResponseMessage((HttpStatusCode)exchange["status"]!.GetValue<int>())
        {
            Content = response is JsonArray events
                ? new StringContent(
                    string.Concat(events.Select(data => $"event: {data!["type"]}\ndata: {data.ToJsonString()}\n\n")), Encoding.UTF8, "text/event-stream")
                : new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json"),
        };
    }
}
