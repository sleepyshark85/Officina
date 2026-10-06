using System.Collections.Concurrent;

namespace Sleepyshark.Officina.Tests;

/// <summary>An in-memory audit sink: keeps the entries it accepts, and refuses those <paramref name="fails"/> picks.</summary>
internal sealed class RecordingSink(Func<AuditEntry, bool>? fails = null) : IAuditSink
{
    private readonly ConcurrentQueue<AuditEntry> entries = new();

    public IReadOnlyList<AuditEntry> Entries => [.. entries];

    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (fails?.Invoke(entry) == true)
        {
            throw new IOException("The audit store is down.");
        }

        entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}
