using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Testing;

/// <summary>A human who gives answers scripted in advance, in order, and remembers what was asked.</summary>
public sealed class ScriptedHuman : IHumanChannel
{
    private readonly ConcurrentQueue<HumanAnswer> answers = new();
    private readonly ConcurrentQueue<HumanRequest> requests = new();

    /// <summary>The requests received so far, in order.</summary>
    public IReadOnlyList<HumanRequest> Requests => [.. requests];

    /// <summary>Adds an answer to the end of the script.</summary>
    public ScriptedHuman Answer(HumanAnswer answer)
    {
        answers.Enqueue(answer ?? throw new ArgumentNullException(nameof(answer)));
        return this;
    }

    public ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
    {
        requests.Enqueue(request ?? throw new ArgumentNullException(nameof(request)));
        return answers.TryDequeue(out var answer)
            ? ValueTask.FromResult(answer)
            : throw new InvalidOperationException($"The scripted human was asked {requests.Count} times but has no answer left. Add one with Answer().");
    }
}
