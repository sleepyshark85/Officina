using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Events;

/// <summary>
/// The live event stream of a runner (EVT-01, DESIGN.md §2). Each event gets its run's next sequence number and is stored before readers
/// see it, unless its kind is configured not to be (EVT-05). Each reader has its own bounded queue; a reader that falls
/// behind is detached and catches up from the stored events, so no reader ever slows an agent (EVT-03, EVT-04).
/// </summary>
[SuppressMessage("Reliability", "CA1001", Justification = "The semaphore never creates a wait handle, so it holds nothing to dispose.")]
public sealed class EventBus
{
    /// <summary>How many events a reader may fall behind before it is detached.</summary>
    private const int QueueLength = 1000;

    private readonly IEventLog log;
    private readonly IReadOnlyList<string> unstored;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim order = new(1, 1);
    private readonly Lock readersLock = new();
    private readonly List<Reader> readers = [];
    private readonly Dictionary<string, long> sequences = [];

    public EventBus(IEventLog log, StorageOptions options, TimeProvider time)
    {
        this.log = log;
        unstored = options.Unstored;
        this.time = time;
    }

    /// <summary>
    /// A run's events after a sequence number: first the stored ones, then live ones as they happen. It ends only when
    /// <paramref name="ct"/> is cancelled or the reader stops reading.
    /// </summary>
    /// <param name="tenant">The run's tenant; another tenant's run yields nothing (SEC-02).</param>
    /// <param name="runId">The run.</param>
    /// <param name="after">The last sequence number the reader has seen; 0 for the start of the run.</param>
    /// <param name="ct">Stops reading.</param>
    public async IAsyncEnumerable<CoreEvent> ReadAsync(string? tenant, string runId, long after, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            // The reader is attached before the stored events are read, so nothing published in between is missed.
            var reader = new Reader(tenant, runId, Channel.CreateBounded<CoreEvent>(QueueLength));
            lock (readersLock)
            {
                readers.Add(reader);
            }

            try
            {
                foreach (var stored in await log.ReadAsync(tenant, runId, after, ct).ConfigureAwait(false))
                {
                    after = stored.Sequence;
                    yield return stored;
                }

                await foreach (var live in reader.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (live.Sequence > after)
                    {
                        after = live.Sequence;
                        yield return live;
                    }
                }
            }
            finally
            {
                lock (readersLock)
                {
                    readers.Remove(reader);
                }
            }

            // The queue was completed because the reader fell behind: catch up from the stored events.
        }
    }

    /// <summary>
    /// Makes the run's next event follow the last one stored, when the run starts again in a new process (RUN-04). Events a
    /// crashed process published before it died keep their numbers, so readers that saw them catch up without a gap or a repeat.
    /// </summary>
    internal async ValueTask ContinueAsync(string? tenant, string runId, CancellationToken ct)
    {
        var stored = await log.ReadAsync(tenant, runId, 0, ct).ConfigureAwait(false);
        await order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            sequences[runId] = Math.Max(sequences.GetValueOrDefault(runId), stored.Count == 0 ? 0 : stored[^1].Sequence);
        }
        finally
        {
            order.Release();
        }
    }

    internal async ValueTask PublishAsync(ToolContext context, EventPayload payload, CancellationToken ct)
    {
        await order.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = sequences.GetValueOrDefault(context.RunId) + 1;
            sequences[context.RunId] = next;
            var coreEvent = new CoreEvent(context.RunId, context.Agent, context.Step, next, time.GetUtcNow(), payload);
            if (!unstored.Contains(payload.Kind))
            {
                await log.AppendAsync(context.Caller.Tenant, coreEvent, ct).ConfigureAwait(false);
            }

            lock (readersLock)
            {
                foreach (var reader in readers.Where(reader => reader.RunId == context.RunId && reader.Tenant == context.Caller.Tenant))
                {
                    if (!reader.Queue.Writer.TryWrite(coreEvent))
                    {
                        reader.Queue.Writer.TryComplete();
                    }
                }
            }
        }
        finally
        {
            order.Release();
        }
    }

    private sealed record Reader(string? Tenant, string RunId, Channel<CoreEvent> Queue);
}
