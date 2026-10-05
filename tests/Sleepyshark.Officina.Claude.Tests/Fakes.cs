using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>
/// The Claude API's side of the network, a system boundary: answers each request with the next scripted HTTP response and
/// keeps the bodies of the requests as sent.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> responses = new();

    public List<string> Requests { get; } = [];

    /// <summary>Answers with a recorded event stream, read from <c>Recordings/</c>.</summary>
    public FakeApi Recorded(string name) => Stream(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Recordings", name)));

    /// <summary>Answers 200 with this server-sent event stream.</summary>
    public FakeApi Stream(string sse) => Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(sse.ReplaceLineEndings("\n"), Encoding.UTF8, "text/event-stream"),
    });

    /// <summary>Answers 200 with the start of this event stream, then drops the connection.</summary>
    public FakeApi Dropped(string sse) => Enqueue(() =>
    {
        var content = new StreamContent(new DroppingStream(Encoding.UTF8.GetBytes(sse.ReplaceLineEndings("\n"))));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    });

    /// <summary>Answers with an API error, as the API sends it.</summary>
    public FakeApi Error(int status, string type, string message = "Something went wrong.", TimeSpan? retryAfter = null) => Enqueue(() =>
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent($$$"""{"type":"error","error":{"type":"{{{type}}}","message":"{{{message}}}"}}""", Encoding.UTF8, "application/json"),
        };
        if (retryAfter is { } wait)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        }

        return response;
    });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        lock (Requests)
        {
            Requests.Add(body);
        }

        return responses.TryDequeue(out var next) ? next() : throw new InvalidOperationException("The fake API has no response left.");
    }

    private FakeApi Enqueue(Func<HttpResponseMessage> response)
    {
        responses.Enqueue(response);
        return this;
    }
}

/// <summary>A response body that ends in a dropped connection once its bytes are read.</summary>
internal sealed class DroppingStream(byte[] bytes) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count) =>
        Position < Length ? base.Read(buffer, offset, count) : throw new IOException("The connection was reset.");

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Position < Length ? base.ReadAsync(buffer, cancellationToken) : ValueTask.FromException<int>(new IOException("The connection was reset."));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}

/// <summary>A clock whose waits end at once, keeping what each wait asked for, so retry tests never sleep.</summary>
internal sealed class InstantTime : TimeProvider
{
    public ConcurrentQueue<TimeSpan> Waits { get; } = new();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Waits.Enqueue(dueTime);
        return System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
}

/// <summary>Server-sent events for the tests, in the shape the Claude API streams them.</summary>
internal static class Sse
{
    public const string Start = """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""";

    public static string Events(params string[] data) =>
        string.Concat(data.Select(json => $"event: {Type(json)}\ndata: {json}\n\n"));

    /// <summary>A whole reply of one text block, streamed in pieces, that stops for <paramref name="reason"/>.</summary>
    public static string Text(string reason = "end_turn", params string[] pieces) => Events(
    [
        Start,
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        .. (pieces.Length == 0 ? ["Hello."] : pieces).Select(piece => $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{piece}}}"}}"""),
        """{"type":"content_block_stop","index":0}""",
        $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{reason}}}","stop_sequence":null},"usage":{"output_tokens":5}}""",
        """{"type":"message_stop"}""",
    ]);

    public static string Error(string type) => $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"Overloaded"}}""";

    private static string Type(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!;
}
