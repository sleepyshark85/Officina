using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Observability;

/// <summary>
/// Traces, metrics and logs (OBS-01, OBS-02, OBS-03), seen through the base library's own listeners, as an exporter
/// sees them. The agent calls <c>read</c>, which fails, then <c>edit</c>, which is denied, and stops.
/// Listeners see every test's runs, so each test looks only at its own run.
/// </summary>
public sealed class TelemetryTests : IDisposable
{
    private const string Content = "Invoice A-17 for ACME";

    /// <summary>Metrics carry no run id, so this test's agent has a name of its own.</summary>
    private const string Observed = "observed";

    private readonly ConcurrentQueue<Activity> activities = new();
    private readonly ConcurrentQueue<(string Name, double Value, Dictionary<string, object?> Tags)> measurements = new();
    private readonly ActivityListener tracing;
    private readonly MeterListener metering = new();
    private readonly LogListener logging = new();
    private readonly TestKit kit;

    public TelemetryTests()
    {
        tracing = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Telemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(tracing);
        metering.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == Telemetry.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        metering.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Measured(instrument, value, tags));
        metering.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Measured(instrument, value, tags));
        metering.Start();

        var read = new FakeTool(ToolKind.Read, run: (_, _) => throw new IOException("disk full"));
        var options = Options(("read", Extension("read")), ("edit", Extension("edit") with { GateExemption = "Tests only." }));
        options = options with
        {
            Policies = new() { PermissionRules = [new() { Tool = "edit", Action = PolicyAction.Deny }] },
            Agents = new Dictionary<string, AgentDefinition> { [Observed] = options.Agents[Agent] with { HandOffOnPolicyGap = false } },
            Capabilities = new() { TaskBoard = new() { Enabled = true } },
        };
        kit = new TestKit(options, new Dictionary<string, ITool> { ["read"] = read, ["edit"] = new FakeTool(ToolKind.Write) });
        kit.Model.CallTools(("read", $$"""{"path":"{{Content}}"}"""))
            .CallTools(("edit", "{}"))
            .Reply(new TextDelta(Content), new UsageReported(new Usage(100, 20, 30, 0)), new Stopped(StopReason.Finished));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        tracing.Dispose();
        metering.Dispose();
        logging.Dispose();
    }

    // OBS-01.
    [Fact]
    public async Task The_turn_its_model_calls_and_tool_calls_are_traced_with_the_GenAI_names()
    {
        var runId = await RunAsync();

        var spans = activities.Where(span => (string?)span.GetTagItem("officina.run.id") == runId).ToList();
        var turn = Assert.Single(spans, span => span.DisplayName == $"invoke_agent {Observed}");
        Assert.Equal(
            ["chat claude-opus-5-5", "execute_tool read", "chat claude-opus-5-5", "execute_tool edit", "chat claude-opus-5-5"],
            spans.Where(span => span.ParentSpanId == turn.SpanId).Select(span => span.DisplayName));
        Assert.All(spans, span => Assert.Equal(Observed, span.GetTagItem("gen_ai.agent.name")));

        var chat = spans.Last(span => span.DisplayName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Equal(("chat", "claude", "claude-opus-5-5", 130L, 20L, 30L, 0L), (chat.GetTagItem("gen_ai.operation.name"),
            chat.GetTagItem("gen_ai.provider.name"), chat.GetTagItem("gen_ai.request.model"), chat.GetTagItem("gen_ai.usage.input_tokens"),
            chat.GetTagItem("gen_ai.usage.output_tokens"), chat.GetTagItem("gen_ai.usage.cache_read.input_tokens"),
            chat.GetTagItem("gen_ai.usage.cache_write.input_tokens")));

        var edit = Assert.Single(spans, span => span.DisplayName == "execute_tool edit");
        Assert.Equal(("NotAuthorised", ActivityStatusCode.Error), (edit.GetTagItem("error.type"), edit.Status));
        var decision = Assert.Single(edit.Events);
        Assert.Equal(("officina.decision", "policies.permissionRules[0]"), (decision.Name, decision.Tags.Single(tag => tag.Key == "officina.decided_by").Value));
        Assert.DoesNotContain(spans.SelectMany(span => span.TagObjects), tag => $"{tag.Value}".Contains(Content, StringComparison.Ordinal));
    }

    // OBS-02.
    [Fact]
    public async Task Tokens_cost_iterations_tool_calls_handoffs_and_latency_are_measured()
    {
        kit.Model.Reply(new Stopped(StopReason.Refused));

        await RunAsync();
        var work = new Work(Observed, Content) { TaskId = "t1" };
        await kit.Runner.Board(null, work.RunId).AddAsync("t1", new() { Title = "Observe" }, "planned", Ct);
        await kit.Runner.RunAsync(work, Ct); // handed off

        var mine = measurements.Where(measurement => (string?)measurement.Tags.GetValueOrDefault("gen_ai.agent.name") == Observed).ToList();
        Assert.Equal(
            ["gen_ai.client.operation.duration", "gen_ai.client.token.usage", "officina.cost", "officina.handoffs", "officina.iterations", "officina.tool.calls"],
            mine.Select(measurement => measurement.Name).Distinct().Order());
        Assert.Contains(mine, measurement => measurement.Name == "gen_ai.client.token.usage" && measurement.Value == 130
            && (string?)measurement.Tags["gen_ai.token.type"] == "input" && (string?)measurement.Tags["gen_ai.request.model"] == "claude-opus-5-5"
            && (string?)measurement.Tags["gen_ai.provider.name"] == "claude");
        Assert.Contains(mine, measurement => measurement.Name == "gen_ai.client.operation.duration"
            && (string?)measurement.Tags["gen_ai.operation.name"] == "chat" && (string?)measurement.Tags["gen_ai.provider.name"] == "claude");
        Assert.Contains(mine, measurement => measurement.Name == "officina.tool.calls" && (string?)measurement.Tags["officina.tool.outcome"] == "Failed");
        Assert.Contains(mine, measurement => measurement.Name == "officina.handoffs" && (string?)measurement.Tags["officina.handoff.reason"] == "ProviderRefusal");
        Assert.Contains(mine, measurement => measurement.Name == "officina.cost" && (string?)measurement.Tags.GetValueOrDefault("officina.task.id") == "t1");
    }

    // OBS-03, TOOL-08.
    [Fact]
    public async Task Logs_identify_the_run_and_agent_keep_a_read_tools_error_detail_and_hold_no_conversation_content()
    {
        var runId = await RunAsync();

        var entries = logging.Entries.Where(entry => entry.Payload![0] as string == runId).ToList();
        Assert.Equal(["ToolFailed", "TurnEnded"], entries.Select(entry => entry.EventName));
        Assert.Equal([runId, Observed, "", "read", "Failed", "IOException: disk full"], entries[0].Payload);
        Assert.Equal([runId, Observed, "Completed", ""], entries[1].Payload);
        Assert.DoesNotContain(entries.SelectMany(entry => entry.Payload!), value => $"{value}".Contains(Content, StringComparison.Ordinal));
    }

    private async Task<string> RunAsync()
    {
        await kit.RunAsync(Observed, Content, Ct);
        return kit.Storage.Runs.Runs[^1].RunId;
    }

    private void Measured<T>(Instrument instrument, T value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct =>
        measurements.Enqueue((instrument.Name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture), new Dictionary<string, object?>(tags.ToArray())));

    private sealed class LogListener : EventListener
    {
        public ConcurrentQueue<EventWrittenEventArgs> Entries { get; } = new();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Sleepyshark-Officina")
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) => Entries.Enqueue(eventData);
    }
}
