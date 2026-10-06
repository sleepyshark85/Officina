using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>The Claude API's side of the network: answers each request with the next scripted response, keeping the bodies sent.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
    private readonly ConcurrentQueue<Func<HttpResponseMessage>> responses = new();
    private readonly List<(string Marker, Queue<Func<HttpResponseMessage>> Responses)> routes = [];

    /// <summary>From now on, answers requests whose body contains <paramref name="marker"/> with their own responses.</summary>
    public FakeApi Route(string marker, Action<FakeApi> responses)
    {
        var own = new FakeApi();
        responses(own);
        routes.Add((marker, new Queue<Func<HttpResponseMessage>>(own.responses)));
        return this;
    }

    /// <summary>Holds each response until this many requests have arrived, so calls are in flight together.</summary>
    public int Together { get; init; } = 1;

    private readonly TaskCompletionSource arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the first request arrives, so a test can start a second call after it.</summary>
    public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<string> Requests { get; } = [];

    /// <summary>Answers with an event stream from <c>Fixtures/</c>, hand-written in the API's SSE format.</summary>
    public FakeApi Fixture(string name) => Stream(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

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
        Queue<Func<HttpResponseMessage>>? routed;
        lock (Requests)
        {
            Requests.Add(body);
            FirstRequest.TrySetResult();
            routed = routes.FirstOrDefault(route => body.Contains(route.Marker, StringComparison.Ordinal)).Responses;
            if (Requests.Count == Together)
            {
                arrived.TrySetResult();
            }
        }

        await arrived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (routed is not null)
        {
            lock (routed)
            {
                return routed.TryDequeue(out var own) ? own() : throw new InvalidOperationException($"The fake API has no response left for this route.");
            }
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

/// <summary>
/// A clock whose waits end at once, recording what each asked for, so retry tests never sleep; a held clock's waits never
/// end. Each wait is kept with the <see cref="Caller"/> that started it.
/// </summary>
internal sealed class InstantTime(bool held = false) : TimeProvider
{
    public static readonly AsyncLocal<string?> Caller = new();

    public ConcurrentQueue<TimeSpan> Waits { get; } = new();

    public ConcurrentQueue<(string? Caller, TimeSpan Wait)> WaitsByCaller { get; } = new();

    /// <summary>Completes when the first wait starts.</summary>
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Waits.Enqueue(dueTime);
        WaitsByCaller.Enqueue((Caller.Value, dueTime));
        Waiting.TrySetResult();
        return System.CreateTimer(callback, state, held ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, period);
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
