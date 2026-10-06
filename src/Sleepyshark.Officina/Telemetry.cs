using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;

namespace Sleepyshark.Officina;

/// <summary>
/// The core's traces and metrics (EVT-02, ARCHITECTURE §7), emitted through .NET's own <see cref="ActivitySource"/> and
/// <see cref="Meter"/>, both named <see cref="SourceName"/>; the host chooses an exporter. One trace per run (a child of
/// the host's current span, if it has one): a run span, with a span per model call and per tool call. Names follow the
/// OpenTelemetry semantic conventions for generative AI where one exists; the rest are under <c>officina.</c>. Message
/// text, tool inputs and results appear only when the agent opts in (EVT-04), and secrets never do (EVT-03).
/// </summary>
public static class Telemetry
{
    /// <summary>The name of the core's activity source and meter.</summary>
    public const string SourceName = "Sleepyshark.Officina";

    internal static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Histogram<long> Tokens = Meter.CreateHistogram<long>(
        "gen_ai.client.token.usage", "{token}", "Tokens per model call, by type: input (all of it, cached or not) and output.");

    private static readonly Histogram<long> CacheTokens = Meter.CreateHistogram<long>(
        "officina.model.cache_tokens", "{token}", "Input tokens per model call read from or written to the cache, by officina.cache.type: read or write. Part of the input tokens.");

    private static readonly Histogram<double> ModelDuration = Meter.CreateHistogram<double>(
        "gen_ai.client.operation.duration", "s", "Duration of a model call, retries included.");

    private static readonly Histogram<double> CacheHitRatio = Meter.CreateHistogram<double>(
        "officina.model.cache_hit_ratio", "1", "The share of a model call's input tokens read from the cache (CTX-05).");

    private static readonly Histogram<double> Cost = Meter.CreateHistogram<double>(
        "officina.model.cost", "{USD}", "What a model call cost, in US dollars, at the model's price (BUD-02).");

    private static readonly Counter<long> Retries = Meter.CreateCounter<long>("officina.model.retries", "{retry}", "Model call retries.");

    private static readonly Histogram<double> ToolDuration = Meter.CreateHistogram<double>(
        "officina.tool.duration", "s", "Duration of a tool call, from its start to its result, approval included.");

    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("officina.tool.calls", "{call}", "Tool calls, by outcome.");

    private static readonly Counter<long> Approvals = Meter.CreateCounter<long>("officina.tool.approvals", "{approval}", "Approvals, by answer.");

    private static readonly Counter<long> Compactions = Meter.CreateCounter<long>(
        "officina.model.compactions", "{compaction}", "Compactions of the conversation by the provider (HIST-04).");

    private static readonly Counter<long> Clearings = Meter.CreateCounter<long>(
        "officina.model.clearings", "{clearing}", "Model calls for which the provider cleared old tool results (HIST-04).");

    private static readonly Counter<long> Runs = Meter.CreateCounter<long>("officina.runs", "{run}", "Runs, by result.");

    private static readonly Counter<long> AuditFailures = Meter.CreateCounter<long>(
        "officina.audit.failures", "{entry}", "Audit entries the sink failed to write (AUD-06).");

    internal static Activity? StartRun(AgentDefinition agent, Conversation conversation, string message) =>
        Start($"invoke_agent {agent.Name}", ActivityKind.Internal, Activity.Current?.Context ?? default, agent, [
            new("gen_ai.operation.name", "invoke_agent"),
            new("gen_ai.agent.name", agent.Name),
            new("gen_ai.conversation.id", conversation.Id),
            new("gen_ai.provider.name", agent.Model.Provider),
            new("gen_ai.request.model", agent.Model.Name),
            .. Content(agent, "gen_ai.input.messages", () => Messages("user", message)),
        ]);

    /// <summary>Ends a run's span and counts it; <paramref name="result"/> is null for a run the host abandoned.</summary>
    internal static void EndRun(Activity? activity, AgentDefinition agent, RunResult? result)
    {
        var (kind, reason, error) = result switch
        {
            null => ("abandoned", null, null),
            Stopped stopped => ("stopped", stopped.Reason.ToString(), null),
            Failed failed => ("failed", failed.Reason.ToString(), failed.Error),
            _ => ("completed", (string?)null, (string?)null),
        };
        Runs.Add(1, [.. Dimensions(agent), new("officina.run.result", kind), new("officina.run.reason", reason)]);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("officina.run.result", kind);
        activity.SetTag("officina.run.reason", reason);
        if (result is not null)
        {
            SetUsage(activity, result.Usage, result.Cost);
            activity.SetTag("officina.run.model_calls", result.ModelCalls);
            activity.SetTag("officina.run.tool_calls", result.ToolCalls);
        }

        if (result is Completed completed)
        {
            SetContent(activity, agent, "gen_ai.output.messages", () => Messages("assistant", completed.Text));
        }

        Stop(activity, agent, error is null ? null : (reason!, error));
    }

    internal static Activity? StartModelCall(AgentDefinition agent, Activity? run) =>
        Start($"chat {agent.Model.Name}", ActivityKind.Client, run?.Context ?? default, agent, [
            new("gen_ai.operation.name", "chat"),
            new("gen_ai.provider.name", agent.Model.Provider),
            new("gen_ai.request.model", agent.Model.Name),
            new("gen_ai.agent.name", agent.Name),
        ]);

    /// <summary>
    /// Ends a model call's span and records its metrics. <paramref name="stop"/> is null when the call failed (then
    /// <paramref name="error"/> says why) or was cancelled.
    /// </summary>
    internal static void EndModelCall(
        Activity? activity, AgentDefinition agent, long started, Usage usage, decimal cost, ModelStopped? stop, string? error, int retries,
        TimeSpan? firstText, Func<string> text)
    {
        var dimensions = Dimensions(agent);
        var input = usage.Input + usage.CacheRead + usage.CacheWrite;

        // A call that reported no tokens, such as one that failed before its reply started, records none.
        if (usage != default)
        {
            Tokens.Record(input, [.. dimensions, new("gen_ai.operation.name", "chat"), new("gen_ai.token.type", "input")]);
            Tokens.Record(usage.Output, [.. dimensions, new("gen_ai.operation.name", "chat"), new("gen_ai.token.type", "output")]);
            CacheTokens.Record(usage.CacheRead, [.. dimensions, new("officina.cache.type", "read")]);
            CacheTokens.Record(usage.CacheWrite, [.. dimensions, new("officina.cache.type", "write")]);
            Cost.Record((double)cost, dimensions);
        }

        if (input > 0)
        {
            CacheHitRatio.Record((double)usage.CacheRead / input, dimensions);
        }

        var errorType = error is null ? null : "model_error";
        ModelDuration.Record(agent.Time.GetElapsedTime(started).TotalSeconds, [.. dimensions, new("gen_ai.operation.name", "chat"), new("error.type", errorType)]);
        if (activity is null)
        {
            return;
        }

        SetUsage(activity, usage, cost);
        activity.SetTag("officina.model.retries", retries);
        if (stop is not null)
        {
            activity.SetTag("gen_ai.response.finish_reasons", new[] { stop.Reason == ModelStopReason.Unknown ? stop.Detail ?? "unknown" : Word(stop.Reason) });
        }

        if (firstText is { } first)
        {
            activity.SetTag("officina.model.time_to_first_token", first.TotalSeconds);
        }

        SetContent(activity, agent, "gen_ai.output.messages", () => Messages("assistant", text()));
        Stop(activity, agent, error is null ? null : ("model_error", error));
    }

    /// <summary>Records a compaction on the model call's span, and counts it.</summary>
    internal static void Compacted(Activity? activity, AgentDefinition agent, CompactionReported compaction)
    {
        Compactions.Add(1, Dimensions(agent));
        activity?.SetTag("officina.compaction.tokens", compaction.Tokens);
        activity?.SetTag("officina.compaction.summary_tokens", compaction.SummaryTokens);
    }

    /// <summary>Records a clearing of old tool results on the model call's span, and counts it.</summary>
    internal static void Cleared(Activity? activity, AgentDefinition agent, ClearingReported clearing)
    {
        Clearings.Add(1, Dimensions(agent));
        activity?.SetTag("officina.clearing.tokens", clearing.Tokens);
        activity?.SetTag("officina.clearing.tool_calls", clearing.ToolCalls);
    }

    /// <summary>Counts a retry of a model call.</summary>
    internal static void Retried(AgentDefinition agent) => Retries.Add(1, Dimensions(agent));

    internal static Activity? StartToolCall(AgentDefinition agent, Activity? run, Tool? tool, ToolCall call) =>
        Start($"execute_tool {call.Name}", ActivityKind.Internal, run?.Context ?? default, agent, [
            new("gen_ai.operation.name", "execute_tool"),
            new("gen_ai.tool.name", call.Name),
            new("gen_ai.tool.call.id", call.Id),
            new("gen_ai.tool.type", "function"),
            new("officina.tool.source", tool?.Source?.Name ?? (tool?.IsMemory == true ? "memory" : "application")),
            new("officina.tool.kind", tool is null ? null : Word(tool.Kind)),
            .. Content(agent, "gen_ai.tool.call.arguments", () => agent.Redact(call.Input)),
        ]);

    /// <summary>Records an approval's answer and how long it was waited for, on the call's span and in the metrics.</summary>
    internal static void Approved(Activity? activity, AgentDefinition agent, ToolCall call, bool approved, TimeSpan wait)
    {
        var answer = approved ? "approved" : "denied";
        Approvals.Add(1, [.. Dimensions(agent), new("gen_ai.tool.name", call.Name), new("officina.tool.approval", answer)]);
        activity?.SetTag("officina.tool.approval", answer);
        activity?.SetTag("officina.tool.approval_wait", wait.TotalSeconds);
    }

    /// <summary>
    /// Ends a tool call's span and records its metrics. <paramref name="length"/> is the result's length before
    /// truncation.
    /// </summary>
    internal static void EndToolCall(
        Activity? activity, AgentDefinition agent, ToolCall call, long started, ToolOutcome outcome, TimeSpan? ran, int length, bool truncated, string result)
    {
        KeyValuePair<string, object?>[] dimensions = [.. Dimensions(agent), new("gen_ai.tool.name", call.Name), new("officina.tool.outcome", Word(outcome))];
        ToolCalls.Add(1, dimensions);
        ToolDuration.Record(agent.Time.GetElapsedTime(started).TotalSeconds, dimensions);
        if (activity is null)
        {
            return;
        }

        activity.SetTag("officina.tool.outcome", Word(outcome));
        activity.SetTag("officina.tool.ran", ran?.TotalSeconds);
        activity.SetTag("officina.tool.truncated", truncated);
        activity.SetTag("officina.tool.result_length", length);
        SetContent(activity, agent, "gen_ai.tool.call.result", () => result);
        Stop(activity, agent, outcome switch
        {
            ToolOutcome.Ok => null,
            ToolOutcome.Blocked => ("audit_unavailable", null),
            _ => ("tool_error", null),
        });
    }

    /// <summary>Counts an audit entry the sink failed to write, and marks the span of the step it recorded (AUD-06).</summary>
    internal static void AuditFailed(Activity? step, AgentDefinition agent, AuditKind kind)
    {
        AuditFailures.Add(1, [.. Dimensions(agent), new("officina.audit.kind", kind.ToString())]);
        step?.AddEvent(new ActivityEvent("officina.audit.failed", agent.Time.GetUtcNow(), new ActivityTagsCollection { ["officina.audit.kind"] = kind.ToString() }));
    }

    /// <summary>
    /// Starts a span at <paramref name="parent"/> without making it the ambient one: a run's spans are parented explicitly,
    /// as the ambient span does not survive across the run's asynchronous steps.
    /// </summary>
    private static Activity? Start(string name, ActivityKind kind, ActivityContext parent, AgentDefinition agent, KeyValuePair<string, object?>[] tags)
    {
        var ambient = Activity.Current;
        var activity = Source.StartActivity(name, kind, parent, tags, startTime: agent.Time.GetUtcNow());
        Activity.Current = ambient;
        return activity;
    }

    private static void Stop(Activity activity, AgentDefinition agent, (string Type, string? Description)? error)
    {
        if (error is { } failed)
        {
            activity.SetTag("error.type", failed.Type);
            activity.SetStatus(ActivityStatusCode.Error, failed.Description is null ? null : agent.Redact(failed.Description));
        }

        var ambient = Activity.Current;
        activity.SetEndTime(agent.Time.GetUtcNow().UtcDateTime);
        activity.Stop();
        Activity.Current = ambient;
    }

    private static KeyValuePair<string, object?>[] Dimensions(AgentDefinition agent) =>
        [new("gen_ai.agent.name", agent.Name), new("gen_ai.provider.name", agent.Model.Provider), new("gen_ai.request.model", agent.Model.Name)];

    private static void SetUsage(Activity activity, Usage usage, decimal cost)
    {
        activity.SetTag("officina.usage.cost", (double)cost);
        activity.SetTag("gen_ai.usage.input_tokens", usage.Input + usage.CacheRead + usage.CacheWrite);
        activity.SetTag("gen_ai.usage.output_tokens", usage.Output);
        activity.SetTag("gen_ai.usage.cache_read.input_tokens", usage.CacheRead);
        activity.SetTag("gen_ai.usage.cache_creation.input_tokens", usage.CacheWrite);
    }

    /// <summary>The content attribute, only when the agent opts in (EVT-04), with its secrets redacted (EVT-03).</summary>
    private static KeyValuePair<string, object?>[] Content(AgentDefinition agent, string name, Func<string> value) =>
        agent.TelemetryContent ? [new(name, agent.Redact(value()))] : [];

    private static void SetContent(Activity activity, AgentDefinition agent, string name, Func<string> value)
    {
        foreach (var (key, content) in Content(agent, name, value))
        {
            activity.SetTag(key, content);
        }
    }

    /// <summary>One text message in the semantic conventions' message format.</summary>
    private static string Messages(string role, string text) =>
        JsonSerializer.Serialize(new[] { new { role, parts = new[] { new { type = "text", content = text } } } });

    private static string Word<T>(T value)
        where T : struct, Enum =>
        string.Concat(value.ToString().Select((letter, index) => char.IsUpper(letter)
            ? (index > 0 ? "_" : "") + char.ToLower(letter, CultureInfo.InvariantCulture)
            : letter.ToString()));
}

/// <summary>How a tool call ended, as its span and metrics name it.</summary>
internal enum ToolOutcome
{
    Ok,
    Error,

    /// <summary>A write that did not run, as its attempt could not be audited (AUD-06).</summary>
    Blocked,
}
