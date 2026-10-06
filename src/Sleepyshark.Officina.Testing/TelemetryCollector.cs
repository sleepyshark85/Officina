using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// Collects the core's spans and measurements in memory while it lives, in place of an exporter. It sees every run in
/// the process, concurrent tests' included, so a test picks its own by agent name.
/// </summary>
public sealed class TelemetryCollector : IDisposable
{
    private readonly ConcurrentQueue<Activity> spans = new();
    private readonly ConcurrentQueue<Measured> measurements = new();
    private readonly ActivityListener activities;
    private readonly MeterListener meters;

    public TelemetryCollector()
    {
        activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(activities);
        meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == Telemetry.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Measure(instrument, value, tags));
        meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Measure(instrument, value, tags));
        meters.Start();
    }

    /// <summary>The ended spans of traces holding runs of agents named <paramref name="agent"/>, in the order they ended.</summary>
    public IReadOnlyList<Activity> Spans(string agent)
    {
        var all = spans.ToList();
        var traces = all.Where(span => span.GetTagItem("gen_ai.operation.name") is "invoke_agent" && (string?)span.GetTagItem("gen_ai.agent.name") == agent)
            .Select(span => span.TraceId)
            .ToHashSet();
        return [.. all.Where(span => traces.Contains(span.TraceId))];
    }

    /// <summary>The measurements of agents named <paramref name="agent"/>, in the order they were made.</summary>
    public IReadOnlyList<Measured> Measurements(string agent) =>
        [.. measurements.Where(measured => measured.Tags.TryGetValue("gen_ai.agent.name", out var name) && (string?)name == agent)];

    public void Dispose()
    {
        activities.Dispose();
        meters.Dispose();
    }

    private void Measure(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        measurements.Enqueue(new Measured(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
}

/// <summary>One measurement: the instrument's name, the value and its tags.</summary>
public sealed record Measured(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);
