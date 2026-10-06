namespace Sleepyshark.Officina;

/// <summary>
/// Where the audit trail goes. A run's entries arrive one at a time, in sequence order. A write that returns is durable;
/// a write that fails throws, and the sink never swallows it.
/// </summary>
public interface IAuditSink
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken);
}
