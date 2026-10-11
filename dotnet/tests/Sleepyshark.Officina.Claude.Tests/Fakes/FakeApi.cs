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
