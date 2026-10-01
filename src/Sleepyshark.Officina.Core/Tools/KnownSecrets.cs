using System.Collections.Concurrent;
using Sleepyshark.Officina.Core.Extensibility;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The secret source tools read through. It remembers every value it hands out, so the pipeline can remove them from
/// whatever the model, the audit log or a human receives (INV-06, SEC-05). A host that passes the same one to the model
/// providers and tool servers has the secrets declared in configuration removed as well: the providers' credentials and
/// the tool servers' environment variables and headers.
/// </summary>
public sealed class KnownSecrets(ISecretSource source) : ISecretSource
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

    /// <summary>The text with every value read so far replaced.</summary>
    internal string Remove(string text) =>
        values.Keys.Aggregate(text, (current, value) => current.Replace(value, Removed, StringComparison.Ordinal));
}
