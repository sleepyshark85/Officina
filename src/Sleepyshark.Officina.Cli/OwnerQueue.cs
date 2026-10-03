using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Cli;

/// <summary>
/// The CLI's human channel: each approval, question and sign-off is numbered and waits in the queue until the owner
/// answers it by its number. Only the asking agent waits, so with nobody at the console the run goes on and requests
/// queue up until their deadline (HITL-05).
/// </summary>
/// <param name="output">Where each request is shown as it arrives.</param>
/// <param name="prefix">What the owner types before a command: nothing in <c>sof run</c>, a slash in <c>sof chat</c>.</param>
internal sealed class OwnerQueue(TextWriter output, string prefix = "") : IHumanChannel
{
    private readonly ConcurrentDictionary<int, (HumanRequest Request, TaskCompletionSource<HumanAnswer> Answer)> waiting = new();
    private int numbered;

    /// <summary>Where requests and everything else for the owner are shown.</summary>
    public TextWriter Output => output;

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

    /// <summary>
    /// Answers a waiting request with a command (approve, deny, change or answer); returns what was wrong, if anything. A
    /// command of the wrong kind is refused and the request keeps waiting, so a reply never approves an approval.
    /// </summary>
    public string? Answer(int number, string command, HumanAnswer answer)
    {
        if (!waiting.TryGetValue(number, out var entry))
        {
            return "error: nothing waits for you with that number.";
        }

        var kind = entry.Request.Kind;
        var fits = command switch
        {
            "approve" => kind != HumanRequestKind.Question,
            "deny" => true, // on a question, it declines
            "change" => kind == HumanRequestKind.Approval,
            _ => kind == HumanRequestKind.Question,
        };
        if (!fits)
        {
            var needs = command switch { "answer" => "a question", "change" => "an approval", _ => "an approval or sign-off" };
            return $"error: #{number} is not {needs}.";
        }

        return entry.Answer.TrySetResult(answer) ? null : "error: nothing waits for you with that number.";
    }

    public string Describe(HumanRequest request) => request.Kind switch
    {
        HumanRequestKind.Approval => Lines($"{request.Agent} asks to run {request.Tool} {request.Arguments?.GetRawText()}: {request.Summary}.", $"Answer with {prefix}approve, {prefix}deny or {prefix}change."),
        HumanRequestKind.Question => Lines($"{request.Agent} asks: {request.Summary}", $"Answer with {prefix}answer or {prefix}deny."),
        _ => Lines($"{request.Agent} needs your sign-off: {request.Summary}", $"Answer with {prefix}approve or {prefix}deny."),
    };

    /// <summary>
    /// A request and how to answer it, on one line; a request of several lines, such as a plan, has its lines indented
    /// under the first and the instruction on a line of its own.
    /// </summary>
    private static string Lines(string request, string instruction) =>
        request.Contains('\n', StringComparison.Ordinal) ? $"{request.ReplaceLineEndings("\n  ")}\n{instruction}" : $"{request} {instruction}";
}
