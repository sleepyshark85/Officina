using System.Runtime.CompilerServices;
using System.Text.Json;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Testing;

/// <summary>A model provider that answers with replies scripted in advance, in order. It needs no network or API key.</summary>
public sealed class ScriptedModelProvider : IModelProvider
{
    private readonly Lock gate = new();
    private readonly Queue<ModelEvent[]> replies = new();
    private readonly List<ModelRequest> requests = [];
    private int toolCalls;

    public ProviderCapabilities Capabilities { get; init; } = ProviderCapabilities.None;

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

    /// <summary>Adds a reply that asks for these tool calls, each with its arguments as JSON.</summary>
    public ScriptedModelProvider CallTools(params (string Tool, string Arguments)[] calls)
    {
        ArgumentNullException.ThrowIfNull(calls);
        return Reply([.. calls.Select(call => new ContentReceived(
            new ToolUseContent($"call-{Interlocked.Increment(ref toolCalls)}", call.Tool, JsonDocument.Parse(call.Arguments).RootElement))),
            new Stopped(StopReason.WantsTools)]);
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
            yield return modelEvent;
        }
    }
}
