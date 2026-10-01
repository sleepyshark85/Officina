using System.Runtime.CompilerServices;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>A model provider that answers with replies scripted in advance, in order. It needs no network or API key.</summary>
public sealed class ScriptedModelProvider : IModelProvider
{
    private readonly Lock gate = new();
    private readonly Queue<string> replies = new();
    private readonly List<ModelRequest> requests = [];

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

    /// <summary>Adds a reply to the end of the script.</summary>
    public ScriptedModelProvider Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (gate)
        {
            replies.Enqueue(text);
        }

        return this;
    }

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string reply;
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

        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new TextDelta(reply);
    }
}
