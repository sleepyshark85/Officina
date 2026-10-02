using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// A model provider that answers with replies scripted in advance, in order, and shortens history as scripted. It needs
/// no network or API key.
/// </summary>
public sealed class ScriptedModelProvider : IModelProvider, IHistoryShortener
{
    private readonly Lock gate = new();
    private readonly Queue<ModelEvent[]> replies = new();
    private readonly Queue<Func<ImmutableArray<Message>, IEnumerable<Message>>> shortenings = new();
    private readonly List<ModelRequest> requests = [];
    private int toolCalls;

    public ProviderCapabilities Capabilities { get; init; } = ProviderCapabilities.None;

    public ProviderCapabilities CapabilitiesOf(string model) => Capabilities;

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

    /// <summary>Adds a reply with this text, after which the model has finished.</summary>
    public ScriptedModelProvider Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Reply(new TextDelta(text), new Stopped(StopReason.Finished));
    }

    /// <summary>Adds a reply that streams these events, in order.</summary>
    public ScriptedModelProvider Reply(params ModelEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (gate)
        {
            replies.Enqueue(events);
        }

        return this;
    }

    /// <summary>
    /// Adds a call that fails with this failure (MDL-05), after streaming these events if there are any, as an error that
    /// arrives mid-stream does.
    /// </summary>
    public ScriptedModelProvider Fail(ModelFailure failure, TimeSpan? retryAfter = null, params ModelEvent[] streamedFirst)
    {
        ArgumentNullException.ThrowIfNull(streamedFirst);
        return Reply([.. streamedFirst, new Failing(new ModelCallException(failure, retryAfter: retryAfter))]);
    }

    /// <summary>Adds a reply that asks for these tool calls, each with its arguments as JSON.</summary>
    public ScriptedModelProvider CallTools(params (string Tool, string Arguments)[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return Reply([.. calls.Select(call => new ContentReceived(
            new ToolUseContent($"call-{Interlocked.Increment(ref toolCalls)}", call.Tool, JsonDocument.Parse(call.Arguments).RootElement))),
            new Stopped(StopReason.WantsTools)]);
    }

    /// <summary>Adds a shortening: what the provider's own mechanism makes of the history it is given.</summary>
    public ScriptedModelProvider Shorten(Func<ImmutableArray<Message>, IEnumerable<Message>> shorten)
    {
        ArgumentNullException.ThrowIfNull(shorten);
        lock (gate)
        {
            shortenings.Enqueue(shorten);
        }

        return this;
    }

    public ValueTask<ImmutableArray<Message>> ShortenAsync(ModelRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (gate)
        {
            return shortenings.TryDequeue(out var shorten)
                ? ValueTask.FromResult<ImmutableArray<Message>>([.. shorten(request.History)])
                : throw new InvalidOperationException("The scripted model was asked to shorten history but has no shortening left. Add one with Shorten().");
        }
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ModelEvent[] reply;
        lock (gate)
        {
            requests.Add(request);
            if (!replies.TryDequeue(out var next))
            {
                throw new InvalidOperationException(
                    $"The scripted model received request {requests.Count} but has no reply left. Add one with Reply().");
            }

            reply = next;
        }

        foreach (var modelEvent in reply)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (modelEvent is Failing failing)
            {
                throw failing.Exception;
            }

            yield return modelEvent;
        }
    }

    private sealed record Failing(ModelCallException Exception) : ModelEvent;
}
