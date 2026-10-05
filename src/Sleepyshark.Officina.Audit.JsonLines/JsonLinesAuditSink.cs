using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sleepyshark.Officina.Audit.JsonLines;

/// <summary>
/// Appends audit entries to a file, one JSON object per line (AUD-04). Each write is flushed to disk before it returns,
/// so an acknowledged entry is durable; a failure throws. Safe for concurrent runs within one process.
/// </summary>
public sealed class JsonLinesAuditSink(string path) : IAuditSink, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string path = Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, Json) + "\n");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using (file.ConfigureAwait(false))
            {
                await file.WriteAsync(line, cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose() => gate.Dispose();
}
