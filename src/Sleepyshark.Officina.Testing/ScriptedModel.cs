using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A model that answers with replies scripted in advance, in order, and records every request (TEST-01). It needs no
/// network or API key, and is safe to call from concurrent runs.
/// </summary>
public sealed class ScriptedModel : IModel
{
    private readonly Lock gate = new();
    private readonly Queue<IEnumerable<ModelEvent>> replies = new();
    private readonly List<ModelRequest> requests = [];

    public string Settings { get; init; } = "scripted";

    /// <summary>The requests received so far, in order.</summary>
    public IReadOnlyList<ModelRequest> Requests
    {
        get
        {
            lock (gate)
            {
                return [.. requests];
            }
        }
    }

    /// <summary>Adds a reply that streams <paramref name="text"/> and ends.</summary>
    public ScriptedModel Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Reply(new TextDelta(text), new BlockReceived(TextBlock(text)), new ModelStopped(ModelStopReason.End));
    }

    /// <summary>Adds a reply that streams these events, in order.</summary>
    public ScriptedModel Reply(params ModelEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Enqueue(events);
    }

    /// <summary>Adds a call that streams <paramref name="streamedFirst"/> and then fails with <paramref name="exception"/>.</summary>
    public ScriptedModel Fail(Exception exception, params ModelEvent[] streamedFirst)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(streamedFirst);
        return Enqueue(ThenThrow(streamedFirst, exception));
    }

    /// <summary>A text block as a provider sends it, with its raw JSON.</summary>
    public static ContentBlock TextBlock(string text) =>
        new(text, JsonSerializer.Serialize(new { type = "text", text }));

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IEnumerable<ModelEvent>? reply;
        lock (gate)
        {
            requests.Add(request);
            replies.TryDequeue(out reply);
        }

        if (reply is null)
        {
            throw new InvalidOperationException($"The scripted model received request {Requests.Count} but has no reply left.");
        }

        foreach (var modelEvent in reply)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return modelEvent;
        }
    }

    private ScriptedModel Enqueue(IEnumerable<ModelEvent> reply)
    {
        lock (gate)
        {
            replies.Enqueue(reply);
        }

        return this;
    }

    private static IEnumerable<ModelEvent> ThenThrow(ModelEvent[] first, Exception exception)
    {
        foreach (var modelEvent in first)
        {
            yield return modelEvent;
        }

        throw exception;
    }
}
