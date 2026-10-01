using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The secret source tools read through. It remembers every value it hands out, so the pipeline can remove them from
/// whatever the model, the audit log or a human receives (INV-06, SEC-05).
/// </summary>
internal sealed class KnownSecrets(ISecretSource source) : ISecretSource
{
    private const string Removed = "[secret]";

    private readonly ConcurrentDictionary<string, bool> values = new(StringComparer.Ordinal);

    public async ValueTask<string> GetAsync(string name, CancellationToken ct)
    {
        var value = await source.GetAsync(name, ct).ConfigureAwait(false);
        if (value.Length > 0)
        {
            values.TryAdd(value, true);
        }

        return value;
    }

    public string Remove(string text) =>
        values.Keys.Aggregate(text, (current, value) => current.Replace(value, Removed, StringComparison.Ordinal));
}
