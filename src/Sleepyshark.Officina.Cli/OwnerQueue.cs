using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The CLI's human channel: each approval, question and sign-off is numbered and waits in the queue until the owner
/// answers it by its number. Only the asking agent waits, so with nobody at the console the run goes on and requests
/// queue up until their deadline (HITL-05).
/// </summary>
/// <param name="output">Where each request is shown as it arrives.</param>
internal sealed class OwnerQueue(TextWriter output) : IHumanChannel
{
    private readonly ConcurrentDictionary<int, (HumanRequest Request, TaskCompletionSource<HumanAnswer> Answer)> waiting = new();
    private int numbered;

    /// <summary>What waits for the owner now, by number.</summary>
    public IEnumerable<(int Number, HumanRequest Request)> Waiting =>
        waiting.OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value.Request));

    public async ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var number = Interlocked.Increment(ref numbered);
        var answer = new TaskCompletionSource<HumanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[number] = (request, answer);
        output.WriteLine($"#{number} {Describe(request)}");
        try
        {
            return await answer.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            waiting.TryRemove(number, out _);
        }
    }

    /// <summary>Answers a waiting request; false when none has that number.</summary>
    public bool Answer(int number, HumanAnswer answer) => waiting.TryGetValue(number, out var entry) && entry.Answer.TrySetResult(answer);

    public static string Describe(HumanRequest request) => request.Kind switch
    {
        HumanRequestKind.Approval => $"{request.Agent} asks to run {request.Tool} {request.Arguments?.GetRawText()}: {request.Summary}. Answer with approve, deny or change.",
        HumanRequestKind.Question => $"{request.Agent} asks: {request.Summary} Answer with answer.",
        _ => $"{request.Agent} needs your sign-off: {request.Summary} Answer with approve or deny.",
    };
}
