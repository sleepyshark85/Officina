using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The human channel as the core uses it: each request gets its deadline, and is published as it starts and ends, so a
/// reader sees what waits for the owner (UX-01). Only the asking agent waits (LOOP-12), and its budget uses no time meanwhile
/// (RUN-05): the deadline bounds each wait.
/// </summary>
/// <param name="human">The host's channel.</param>
/// <param name="events">Where the waits are published.</param>
/// <param name="timeout">How long a request waits for an answer (HITL-02).</param>
/// <param name="time">The clock for the deadline.</param>
internal sealed class OwnerChannel(IHumanChannel human, EventBus events, TimeSpan timeout, TimeProvider time)
{
    /// <summary>Asks, and waits for the answer until the deadline.</summary>
    /// <returns>The answer, or null when none came by the deadline.</returns>
    public async Task<HumanAnswer?> AskAsync(ToolContext context, HumanRequest request, CancellationToken ct)
    {
        await events.PublishAsync(context, new HumanAsked(request.Kind, request.Summary, request.Tool), ct).ConfigureAwait(false);
        using var waiting = context.WaitForOwner?.Invoke();
        using var deadline = new CancellationTokenSource(timeout, time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        HumanAnswer? answer = null;
        try
        {
            // Waiting on the token too ends the wait at the deadline even if the channel ignores cancellation.
            answer = await human.AskAsync(request, wait.Token).AsTask().WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
        }
        finally
        {
            // Also when the agent is cancelled meanwhile: a resumed run reads from the events when its agents waited (INV-07).
            await events.PublishAsync(context, new HumanAnswered(request.Kind, answer?.Approved == true, answer is null && deadline.IsCancellationRequested, request.Tool), CancellationToken.None)
                .ConfigureAwait(false);
        }

        return answer;
    }

    /// <summary>A request from the agent of <paramref name="context"/>, with its deadline from now.</summary>
    public HumanRequest Request(ToolContext context, HumanRequestKind kind, string summary) => new(kind, context.AgentId, summary, time.GetUtcNow() + timeout);

    /// <summary>What the agent is told when nobody answered in time.</summary>
    public string NoAnswer => $"no answer within {timeout}";
}
