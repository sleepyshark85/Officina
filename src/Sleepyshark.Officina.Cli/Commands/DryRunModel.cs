using System.Runtime.CompilerServices;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli.Commands;

/// <summary>The scripted model of <c>sof config dry-run</c>: it answers with the given replies in order, then with a fixed one.</summary>
internal sealed class DryRunModel(IReadOnlyList<string> replies) : IModelProvider
{
    private readonly Queue<string> replies = new(replies);
    private readonly List<ModelRequest> requests = [];

    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    public IReadOnlyList<ModelRequest> Requests => requests;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        requests.Add(request);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        yield return new TextDelta(replies.TryDequeue(out var reply) ? reply : "(scripted reply)");
    }
}
