using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Load.Tests;

/// <summary>
/// The load tests (TEST-30) measure wall-clock time, so they run one at a time, apart from the other tests of this assembly.
/// Each warms up first and reports what it measured in the test output; the targets are those of REQUIREMENTS.md.
/// </summary>
[CollectionDefinition(nameof(Load), DisableParallelization = true)]
public sealed class Load;

internal static class Measure
{
    public static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The value below which <paramref name="percent"/>% of the values fall (nearest rank).</summary>
    public static double Percentile(IReadOnlyCollection<double> values, double percent)
    {
        var sorted = values.Order().ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(percent / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Writes a distribution of milliseconds to the test output, and returns its 95th percentile.</summary>
    public static double Report(string what, IReadOnlyCollection<double> milliseconds)
    {
        var p95 = Percentile(milliseconds, 95);
        Write(string.Create(CultureInfo.InvariantCulture,
            $"{what}: n={milliseconds.Count}, p50={Percentile(milliseconds, 50):0.000} ms, p95={p95:0.000} ms, p99={Percentile(milliseconds, 99):0.000} ms, max={milliseconds.Max():0.000} ms"));
        return p95;
    }

    public static void Write(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>A runner as a host wires one, over a model provider that stands in for the real one.</summary>
    public static AgentRunner Runner(
        OfficinaOptions options, IModelProvider model, IStorage storage, IReadOnlyDictionary<string, ITool>? tools = null, IHumanChannel? human = null) =>
        new(
            options,
            options.Providers.Keys.ToDictionary(name => name, _ => model),
            storage,
            tools ?? new Dictionary<string, ITool>(),
            new Dictionary<string, IGate>(),
            new Dictionary<string, ICheck>(),
            new Dictionary<string, IKnowledgeSource>(),
            human ?? new ScriptedHuman(),
            new InMemorySecretSource(new Dictionary<string, string>()),
            TimeProvider.System);
}

/// <summary>
/// A model provider that times another: when each call starts, and how long the call spends inside the provider, that is in
/// the model, as opposed to the core handling what it streams.
/// </summary>
internal sealed class TimedProvider(IModelProvider inner) : IModelProvider
{
    public ConcurrentQueue<(long Started, long Inside)> Calls { get; } = new();

    /// <summary>When each text delta was handed to the core.</summary>
    public ConcurrentQueue<long> TextSent { get; } = new();

    public ProviderCapabilities CapabilitiesOf(string model) => inner.CapabilitiesOf(model);

    public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        long inside = 0;
        await using var events = inner.StreamAsync(request, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                var asked = Stopwatch.GetTimestamp();
                var more = await events.MoveNextAsync().ConfigureAwait(false);
                inside += Stopwatch.GetTimestamp() - asked;
                if (!more)
                {
                    break;
                }

                if (events.Current is TextDelta)
                {
                    TextSent.Enqueue(Stopwatch.GetTimestamp());
                }

                yield return events.Current;
            }
        }
        finally
        {
            Calls.Enqueue((started, inside));
        }
    }
}
