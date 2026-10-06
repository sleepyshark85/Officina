using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>Traces and metrics of scripted runs, collected in memory.</summary>
public class TelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] ToolUse = ["tool_use"];

    [Fact]
    public async Task A_run_is_one_trace_with_a_span_per_model_call_and_per_tool_call_and_its_audit_entries_point_into_it()
    {
        using var telemetry = new TelemetryCollector();
        var time = new FakeTimeProvider(Start);
        var sink = new RecordingSink();
        var model = new ScriptedModel { Provider = "acme", Name = "acme-large", Price = new ModelPrice(4m, 20m, 0.2m, 5m, 8m) }
            .Reply(
                new TextDelta("Looking."), new BlockReceived(ScriptedModel.TextBlock("Looking.")),
                new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall("c1", "search", """{"query":"x"}"""))),
                new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall("c2", "order", "{}"))),
                new UsageReceived(new Usage(100, 20, 300, 50)), new ModelStopped(ModelStopReason.ToolUse))
            .Reply(
                new TextDelta("Ordered."), new BlockReceived(ScriptedModel.TextBlock("Ordered.")),
                new UsageReceived(new Usage(10, 5, 450, 0)), new ModelStopped(ModelStopReason.End));
        var agent = Agents.With(model, tools: [Agents.SearchTool(), Agents.Tool("order", kind: ToolKind.Write, needsApproval: true)]) with
        {
            Name = Unique(),
            AuditSink = sink,
            Time = time,
            Approver = new WaitingApprover(time, TimeSpan.FromSeconds(3)),
        };
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "Order x.", cancellationToken: Ct);

        var spans = telemetry.Spans(agent.Name);
        var run = Assert.Single(spans, span => span.OperationName == $"invoke_agent {agent.Name}");
        Assert.Equal(default, run.ParentSpanId);
        Assert.All(spans, span => Assert.Equal(run.TraceId, span.TraceId));
        Assert.All(spans.Where(span => span != run), span => Assert.Equal(run.SpanId, span.ParentSpanId));
        Assert.Equal(
            ["chat acme-large", "execute_tool order", "execute_tool search", "chat acme-large"],
            spans.Where(span => span != run).OrderBy(span => span.StartTimeUtc).ThenBy(span => span.OperationName, StringComparer.Ordinal).Select(span => span.OperationName));
        Assert.Equal(Start.UtcDateTime, run.StartTimeUtc);
        Assert.Equal(TimeSpan.FromSeconds(3), run.Duration);

        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["gen_ai.operation.name"] = "invoke_agent",
                ["gen_ai.agent.name"] = agent.Name,
                ["gen_ai.conversation.id"] = conversation.Id,
                ["gen_ai.provider.name"] = "acme",
                ["gen_ai.request.model"] = "acme-large",
                ["officina.run.id"] = sink.Entries[0].Run,
                ["officina.run.result"] = "completed",
                ["gen_ai.usage.input_tokens"] = 910L,
                ["gen_ai.usage.output_tokens"] = 25L,
                ["gen_ai.usage.cache_read.input_tokens"] = 750L,
                ["gen_ai.usage.cache_creation.input_tokens"] = 50L,
                ["officina.usage.cost"] = 0.00134,
                ["officina.run.model_calls"] = 2,
                ["officina.run.tool_calls"] = 2,
            },
            Tags(run));

        var firstCall = spans.First(span => span.OperationName.StartsWith("chat", StringComparison.Ordinal));
        Assert.Equal(ActivityKind.Client, firstCall.Kind);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["gen_ai.operation.name"] = "chat",
                ["gen_ai.provider.name"] = "acme",
                ["gen_ai.request.model"] = "acme-large",
                ["gen_ai.agent.name"] = agent.Name,
                ["gen_ai.usage.input_tokens"] = 450L,
                ["gen_ai.usage.output_tokens"] = 20L,
                ["gen_ai.usage.cache_read.input_tokens"] = 300L,
                ["gen_ai.usage.cache_creation.input_tokens"] = 50L,
                ["officina.usage.cost"] = 0.00111,
                ["officina.model.retries"] = 0,
                ["gen_ai.response.finish_reasons"] = ToolUse,
                ["officina.model.time_to_first_token"] = 0.0,
            },
            Tags(firstCall));

        var order = spans.Single(span => span.OperationName == "execute_tool order");
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["gen_ai.operation.name"] = "execute_tool",
                ["gen_ai.tool.name"] = "order",
                ["gen_ai.tool.call.id"] = "c2",
                ["gen_ai.tool.type"] = "function",
                ["officina.tool.source"] = "application",
                ["officina.tool.kind"] = "write",
                ["officina.tool.approval"] = "approved",
                ["officina.tool.approval_wait"] = 3.0,
                ["officina.tool.outcome"] = "ok",
                ["officina.tool.ran"] = 0.0,
                ["officina.tool.truncated"] = false,
                ["officina.tool.result_length"] = 2,
            },
            Tags(order));
        Assert.Equal(TimeSpan.FromSeconds(3), order.Duration);
        Assert.Equal(ActivityStatusCode.Unset, order.Status);
        Assert.DoesNotContain("officina.tool.approval", Tags(spans.Single(span => span.OperationName == "execute_tool search")).Keys);

        // Each entry carries the trace, and the span of the step it records.
        Assert.All(sink.Entries, entry => Assert.Equal(run.TraceId.ToHexString(), entry.TraceId));
        Assert.Equal(
            [
                (AuditKind.RunStarted, run.SpanId), (AuditKind.ToolStarted, Span("search")), (AuditKind.ToolEnded, Span("search")),
                (AuditKind.ApprovalAsked, order.SpanId), (AuditKind.ApprovalAnswered, order.SpanId), (AuditKind.ToolStarted, order.SpanId),
                (AuditKind.ToolEnded, order.SpanId), (AuditKind.RunEnded, run.SpanId),
            ],
            sink.Entries.Select(entry => (entry.Kind, ActivitySpanId.CreateFromString(entry.SpanId))));

        ActivitySpanId Span(string tool) => spans.Single(span => span.OperationName == $"execute_tool {tool}").SpanId;
    }

    [Fact]
    public async Task Metrics_count_tokens_by_type_cost_the_cache_hit_ratio_tool_outcomes_approvals_and_results_by_agent_and_model()
    {
        using var telemetry = new TelemetryCollector();
        var model = new ScriptedModel { Price = new ModelPrice(4m, 20m, 0.2m, 5m, 8m) }
            .Reply(
                new BlockReceived(ScriptedModel.ToolCallBlock(new ToolCall("c1", "save", "{}"))),
                new UsageReceived(new Usage(100, 20, 300, 0)), new ModelStopped(ModelStopReason.ToolUse))
            .Reply(new BlockReceived(ScriptedModel.TextBlock("Not saved.")), new UsageReceived(new Usage(0, 5, 400, 0)), new ModelStopped(ModelStopReason.End));
        var agent = Agents.With(model, tools: Agents.Tool("save", kind: ToolKind.Write, needsApproval: true)) with
        {
            Name = Unique(),
            Approver = new ScriptedApprover().Answer(Approval.Denied("no")),
        };

        await agent.RunAsync(new Conversation(), "Save.", cancellationToken: Ct);

        var measured = telemetry.Measurements(agent.Name);
        Assert.All(measured, each => Assert.Equal(("scripted", "scripted"), (each.Tags["gen_ai.provider.name"], each.Tags["gen_ai.request.model"])));
        Assert.Equal(
            [("input", 400.0), ("output", 20), ("input", 400), ("output", 5)],
            measured.Where(each => each.Instrument == "gen_ai.client.token.usage").Select(each => ((string)each.Tags["gen_ai.token.type"]!, each.Value)));
        Assert.Equal(
            [("read", 300.0), ("write", 0), ("read", 400), ("write", 0)],
            measured.Where(each => each.Instrument == "officina.model.cache_tokens").Select(each => ((string)each.Tags["officina.cache.type"]!, each.Value)));
        Assert.Equal([0.75, 1.0], measured.Where(each => each.Instrument == "officina.model.cache_hit_ratio").Select(each => each.Value));
        Assert.Equal([0.00086, 0.00018], measured.Where(each => each.Instrument == "officina.model.cost").Select(each => each.Value));
        Assert.Equal(2, measured.Count(each => each.Instrument == "gen_ai.client.operation.duration" && each.Tags["error.type"] is null));
        Assert.Equal(("save", "error"), Single(measured, "officina.tool.calls", "gen_ai.tool.name", "officina.tool.outcome"));
        Assert.Equal(("save", "error"), Single(measured, "officina.tool.duration", "gen_ai.tool.name", "officina.tool.outcome"));
        Assert.Equal(("save", "denied"), Single(measured, "officina.tool.approvals", "gen_ai.tool.name", "officina.tool.approval"));
        Assert.Equal("tool_error", telemetry.Spans(agent.Name).Single(span => span.OperationName == "execute_tool save").GetTagItem("error.type"));
        Assert.Equal(("completed", null), Single(measured, "officina.runs", "officina.run.result", "officina.run.reason"));
    }

    [Fact]
    public async Task A_retry_is_counted_even_before_anything_streamed_and_the_failed_attempt_s_tokens_are_kept()
    {
        using var telemetry = new TelemetryCollector();
        var model = new ScriptedModel().Reply(
            new UsageReceived(new Usage(10, 1, 0, 0)), new ModelRetried(),
            new TextDelta("Hi."), new BlockReceived(ScriptedModel.TextBlock("Hi.")), new UsageReceived(new Usage(10, 3, 0, 0)), new ModelStopped(ModelStopReason.End));
        var agent = Agents.With(model) with { Name = Unique() };

        var events = await Agents.CollectAsync(agent.StreamAsync(new Conversation(), "Hi", cancellationToken: Ct));

        Assert.DoesNotContain(events, runEvent => runEvent is ReplyRestarted);
        Assert.Equal(new Usage(20, 4, 0, 0), Assert.IsType<RunEnded>(events[^1]).Result.Usage);
        var call = telemetry.Spans(agent.Name).Single(span => span.OperationName == "chat scripted");
        Assert.Equal((1, 20L, 4L), ((int)call.GetTagItem("officina.model.retries")!, (long)call.GetTagItem("gen_ai.usage.input_tokens")!, (long)call.GetTagItem("gen_ai.usage.output_tokens")!));
        Assert.Equal(1, telemetry.Measurements(agent.Name).Single(each => each.Instrument == "officina.model.retries").Value);
    }

    [Fact]
    public async Task Time_to_first_token_is_measured_on_the_attempt_that_succeeded()
    {
        using var telemetry = new TelemetryCollector();
        var time = new FakeTimeProvider(Start);
        var model = new ScriptedModel().Reply(new TextDelta("Hel"), new ModelRetried(), new TextDelta("Hello."), new BlockReceived(ScriptedModel.TextBlock("Hello.")), new ModelStopped(ModelStopReason.End));
        var agent = Agents.With(model) with { Name = Unique(), Time = time };

        await foreach (var runEvent in agent.StreamAsync(new Conversation(), "Hi", cancellationToken: Ct))
        {
            if (runEvent is ReplyRestarted)
            {
                time.Advance(TimeSpan.FromSeconds(2));
            }
        }

        Assert.Equal(2.0, telemetry.Spans(agent.Name).Single(span => span.OperationName == "chat scripted").GetTagItem("officina.model.time_to_first_token"));
    }

    [Fact]
    public async Task A_run_the_host_abandons_is_counted_and_its_span_ended_on_the_agent_s_clock()
    {
        using var telemetry = new TelemetryCollector();
        var time = new FakeTimeProvider(Start);
        var agent = Agents.With(new ScriptedModel().Reply("Hello.")) with { Name = Unique(), Time = time };

        await foreach (var runEvent in agent.StreamAsync(new Conversation(), "Hi", cancellationToken: Ct))
        {
            time.Advance(TimeSpan.FromSeconds(1));
            break;
        }

        var run = telemetry.Spans(agent.Name).Single(span => span.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal));
        Assert.Equal(("abandoned", TimeSpan.FromSeconds(1)), ((string)run.GetTagItem("officina.run.result")!, run.Duration));
        Assert.Equal("abandoned", telemetry.Measurements(agent.Name).Single(each => each.Instrument == "officina.runs").Tags["officina.run.result"]);
    }

    [Fact]
    public async Task A_failed_model_call_marks_its_span_and_the_run_s_without_the_secret()
    {
        using var telemetry = new TelemetryCollector();
        var model = new ScriptedModel().Fail(new IOException("Upstream refused key s3cret."), new TextDelta("Hel"));
        var agent = Agents.With(model) with { Name = Unique(), Secrets = ["s3cret"] };

        var result = await agent.RunAsync(new Conversation(), "Hi", cancellationToken: Ct);

        Assert.Equal("Upstream refused key [redacted].", Assert.IsType<Failed>(result).Error);
        var spans = telemetry.Spans(agent.Name);
        Assert.All(spans, span => Assert.Equal((ActivityStatusCode.Error, "Upstream refused key [redacted]."), (span.Status, span.StatusDescription)));
        Assert.Equal(["model_error", "ModelError"], spans.Select(span => (string)span.GetTagItem("error.type")!));
        Assert.Equal("model_error", telemetry.Measurements(agent.Name).Single(each => each.Instrument == "gen_ai.client.operation.duration").Tags["error.type"]);
    }

    [Fact]
    public async Task An_audit_sink_failure_and_a_write_it_blocks_show_in_telemetry()
    {
        using var telemetry = new TelemetryCollector();
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "save", "{}")).Reply("Not saved.");
        var agent = Agents.With(model, tools: Agents.Tool("save", kind: ToolKind.Write)) with
        {
            Name = Unique(),
            AuditSink = new RecordingSink(entry => entry.Kind is AuditKind.ToolStarted or AuditKind.RunStarted),
        };

        await agent.RunAsync(new Conversation(), "Save.", cancellationToken: Ct);

        var spans = telemetry.Spans(agent.Name);
        var save = spans.Single(span => span.OperationName == "execute_tool save");
        Assert.Equal(("blocked", "audit_unavailable", ActivityStatusCode.Error), ((string)save.GetTagItem("officina.tool.outcome")!, (string)save.GetTagItem("error.type")!, save.Status));
        Assert.Equal([("officina.audit.failed", "ToolStarted")], save.Events.Select(each => (each.Name, (string)each.Tags.Single().Value!)));
        Assert.Equal(
            [("officina.audit.failed", "RunStarted")],
            spans.Single(span => span.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal)).Events.Select(each => (each.Name, (string)each.Tags.Single().Value!)));
        var measured = telemetry.Measurements(agent.Name);
        Assert.Equal(["RunStarted", "ToolStarted"], measured.Where(each => each.Instrument == "officina.audit.failures").Select(each => (string)each.Tags["officina.audit.kind"]!));
        Assert.Equal("blocked", measured.Single(each => each.Instrument == "officina.tool.calls").Tags["officina.tool.outcome"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Telemetry_carries_no_text_unless_the_agent_opts_in_and_never_a_secret(bool optIn)
    {
        using var telemetry = new TelemetryCollector();
        var model = new ScriptedModel()
            .CallTools(new ToolCall("c1", "search", """{"query":"input-text hunter2"}"""))
            .Reply("answer-text");
        var search = Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) => Task.FromResult(new ToolOutput("result-text hunter2")));
        var agent = Agents.With(model, tools: search) with { Name = Unique(), Secrets = ["hunter2"], TelemetryContent = optIn };

        await agent.RunAsync(new Conversation(), "question-text hunter2", cancellationToken: Ct);

        var dump = Dump(telemetry.Spans(agent.Name)) + string.Concat(telemetry.Measurements(agent.Name).SelectMany(each => each.Tags.Values));
        Assert.DoesNotContain("hunter2", dump, StringComparison.Ordinal);
        foreach (var text in new[] { "question-text", "input-text", "result-text", "answer-text" })
        {
            Assert.Equal(optIn, dump.Contains(text, StringComparison.Ordinal));
        }

        if (optIn)
        {
            var run = telemetry.Spans(agent.Name).Single(span => span.OperationName.StartsWith("invoke_agent", StringComparison.Ordinal));
            Assert.Equal("""[{"role":"user","parts":[{"type":"text","content":"question-text [redacted]"}]}]""", run.GetTagItem("gen_ai.input.messages"));
        }
    }

    /// <summary>Everything a span carries, as text: its name, tags, events and status.</summary>
    internal static string Dump(IEnumerable<Activity> spans) => string.Join('\n', spans.Select(span =>
        $"{span.OperationName} {span.StatusDescription} {JsonSerializer.Serialize(span.TagObjects.Select(tag => $"{tag.Key}={JsonSerializer.Serialize(tag.Value)}"))} "
        + string.Concat(span.Events.Select(each => $"{each.Name} {JsonSerializer.Serialize(each.Tags.Select(tag => $"{tag.Key}={tag.Value}"))}"))));

    private static string Unique() => $"agent-{Guid.NewGuid():N}";

    private static Dictionary<string, object?> Tags(Activity span) => span.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value);

    private static (object?, object?) Single(IEnumerable<Measured> measured, string instrument, string first, string second)
    {
        var one = measured.Single(each => each.Instrument == instrument);
        return (one.Tags[first], one.Tags[second]);
    }

    /// <summary>A human who takes <paramref name="wait"/> to approve.</summary>
    private sealed class WaitingApprover(FakeTimeProvider time, TimeSpan wait) : IApprover
    {
        public Task<Approval> ApproveAsync(Tool tool, ToolCall toolCall, CancellationToken cancellationToken)
        {
            time.Advance(wait);
            return Task.FromResult(Approval.Granted);
        }
    }
}
