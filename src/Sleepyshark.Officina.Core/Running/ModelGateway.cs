using System.Runtime.CompilerServices;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Running;

/// <summary>
/// Where every model call goes. It shares each provider among all agents, retries the calls that fail for a reason that
/// may pass, and moves to a fallback profile when a profile stays unavailable.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>MDL-08, CLD-10: a provider serves at most <c>maxConcurrentCalls</c> calls at once, for all agents together. Calls
/// over it wait in turn, so no agent is starved: an agent's next call goes to the back of the line. The team lead's calls
/// wait in a line of their own, which is served first.</item>
/// <item>REL-01: a transient or rate-limited failure is tried again after a wait that doubles each time, or the wait the
/// provider asks for if longer. A provider that asks for a wait is not called by any agent until it passes. After
/// <c>maxAttempts</c> calls the profile is given up on.</item>
/// <item>MDL-04: the profile's fallbacks are tried in order, each with its own attempts. A failure of another kind, such as
/// an invalid request, is not helped by another model, so it is not retried.</item>
/// </list>
/// </remarks>
public sealed class ModelGateway
{
    private readonly OfficinaOptions options;
    private readonly IReadOnlyDictionary<string, IModelProvider> providers;
    private readonly TimeProvider time;
    private readonly Dictionary<string, Line> lines;
    private readonly HashSet<string> leads;

    /// <param name="options">The configuration.</param>
    /// <param name="providers">The provider implementations, by their name in <c>providers</c>.</param>
    /// <param name="time">The clock for the waits.</param>
    public ModelGateway(OfficinaOptions options, IReadOnlyDictionary<string, IModelProvider> providers, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(time);
        this.options = options;
        this.providers = providers;
        this.time = time;
        lines = options.Providers.ToDictionary(provider => provider.Key, provider => new Line(provider.Value.MaxConcurrentCalls ?? int.MaxValue, time));
        leads = [.. options.Agents.Values.Where(agent => agent.Pattern is not null)
            .SelectMany(agent => agent.Pattern!.Nested(""))
            .Where(nested => nested.Pattern.Type == PatternOptions.Team)
            .Select(nested => nested.Pattern.Lead)
            .OfType<string>()];
    }

    /// <summary>What the profile's model supports (MDL-06).</summary>
    public ProviderCapabilities CapabilitiesOf(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return providers[profile.Provider].CapabilitiesOf(profile.Model);
    }

    /// <summary>
    /// Makes a model call for an agent. A <see cref="FallbackUsed"/> event tells the caller that the events after it are the
    /// fallback's; a <see cref="ReplyRestarted"/> event, that the reply it had so far is void.
    /// </summary>
    /// <param name="agent">The agent that calls.</param>
    /// <param name="request">The request, for the agent's own profile.</param>
    /// <param name="ct">Cancels the call, and any wait.</param>
    /// <exception cref="ModelCallException">The call failed, and no retry or fallback is left for it.</exception>
    public async IAsyncEnumerable<ModelEvent> StreamAsync(string agent, ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var lead = leads.Contains(agent);
        var candidates = request.Profile.Fallbacks.Select(name => (Name: (string?)name, Profile: options.Models[name])).Prepend((null, request.Profile)).ToList();
        ModelCallException? failure = null;
        var sent = false;
        foreach (var (candidate, (name, profile)) in candidates.Index())
        {
            if (candidate > 0)
            {
                yield return new FallbackUsed(name!, profile, failure!.Failure);
            }

            var retry = options.Providers[profile.Provider].Retry;
            for (var attempt = 1; attempt <= retry.MaxAttempts; attempt++)
            {
                if (attempt > 1)
                {
                    await Task.Delay(Wait(retry, attempt, failure!), time, ct).ConfigureAwait(false);
                }

                failure = null;
                var line = lines[profile.Provider];
                using var turn = await line.EnterAsync(lead, ct).ConfigureAwait(false);
                var events = providers[profile.Provider].StreamAsync(name is null ? request : request with { Profile = profile }, ct).GetAsyncEnumerator(ct);
                await using var _ = events.ConfigureAwait(false);
                while (failure is null)
                {
                    bool more;
                    try
                    {
                        more = await events.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (ModelCallException exception) when (exception.Failure is ModelFailure.Transient or ModelFailure.RateLimited)
                    {
                        failure = exception;
                        break;
                    }

                    if (!more)
                    {
                        yield break;
                    }

                    sent = true;
                    yield return events.Current;
                }

                if (failure.RetryAfter is { } asked)
                {
                    line.PauseFor(asked);
                }

                if (sent && (attempt < retry.MaxAttempts || candidate < candidates.Count - 1))
                {
                    sent = false;
                    yield return new ReplyRestarted();
                }
            }
        }

        throw failure!;
    }

    /// <summary>The wait before attempt <paramref name="attempt"/>: twice as long each time, or what the provider asked for if longer.</summary>
    private static TimeSpan Wait(RetryOptions retry, int attempt, ModelCallException failure)
    {
        var progressive = TimeSpan.FromSeconds(Math.Min(retry.MaxDelay.TotalSeconds, retry.InitialDelay.TotalSeconds * Math.Pow(2, attempt - 2)));
        return failure.RetryAfter is { } asked && asked > progressive ? asked : progressive;
    }

    /// <summary>One provider's share of the account: who may call now, and whether it has asked to be left alone.</summary>
    private sealed class Line(int limit, TimeProvider time)
    {
        private readonly Lock gate = new();
        private readonly LinkedList<TaskCompletionSource> leadsWaiting = new();
        private readonly LinkedList<TaskCompletionSource> othersWaiting = new();
        private int free = limit;
        private DateTimeOffset pausedUntil;

        /// <summary>Waits for a place in the line, then for any pause the provider asked for.</summary>
        public async Task<Place> EnterAsync(bool lead, CancellationToken ct)
        {
            LinkedListNode<TaskCompletionSource>? waiting = null;
            var queue = lead ? leadsWaiting : othersWaiting;
            lock (gate)
            {
                if (free > 0 && leadsWaiting.Count == 0 && othersWaiting.Count == 0)
                {
                    free--;
                }
                else
                {
                    waiting = queue.AddLast(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                }
            }

            if (waiting is not null)
            {
                try
                {
                    await waiting.Value.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (gate)
                    {
                        if (waiting.List is not null)
                        {
                            queue.Remove(waiting);
                        }
                        else
                        {
                            Release(); // The place was handed over as the wait was cancelled.
                        }
                    }

                    throw;
                }
            }

            var place = new Place(this);
            try
            {
                while (Remaining() is { Ticks: > 0 } remaining)
                {
                    await Task.Delay(remaining, time, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                place.Dispose();
                throw;
            }

            return place;
        }

        /// <summary>The provider asked for no calls for this long, so no agent calls until it passes (REL-01).</summary>
        public void PauseFor(TimeSpan asked)
        {
            lock (gate)
            {
                pausedUntil = new[] { pausedUntil, time.GetUtcNow() + asked }.Max();
            }
        }

        private TimeSpan Remaining()
        {
            lock (gate)
            {
                return pausedUntil - time.GetUtcNow();
            }
        }

        private void Release()
        {
            // Lead first; otherwise the longest waiting.
            var next = leadsWaiting.First ?? othersWaiting.First;
            if (next is null)
            {
                free++;
                return;
            }

            next.List!.Remove(next);
            next.Value.SetResult();
        }

        public sealed class Place(Line line) : IDisposable
        {
            public void Dispose()
            {
                lock (line.gate)
                {
                    line.Release();
                }
            }
        }
    }
}
