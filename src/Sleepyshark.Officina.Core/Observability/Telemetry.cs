using System.Diagnostics;
using System.Diagnostics.Metrics;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Tools;

namespace Sleepyshark.Officina.Core.Observability;

/// <summary>
/// Traces and metrics (OBS-01, OBS-02), with the names of the OpenTelemetry GenAI semantic conventions where they have
/// one, and <c>officina.*</c> names otherwise. The host exports them by subscribing to <see cref="Name"/>. Neither
/// carries conversation content.
/// </summary>
public static class Telemetry
{
    /// <summary>The name of the activity source and of the meter.</summary>
    public const string Name = "Sleepyshark.Officina";

    internal static readonly ActivitySource Source = new(Name, CoreVersion.Value);

    private static readonly Meter Meter = new(Name, CoreVersion.Value);
    private static readonly Histogram<long> Tokens = Meter.CreateHistogram<long>("gen_ai.client.token.usage", "{token}", "Tokens used by a model call.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("gen_ai.client.operation.duration", "s", "How long a model or tool call took.");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>("officina.cost", "USD", "What model calls cost.");
    private static readonly Counter<long> Iterations = Meter.CreateCounter<long>("officina.iterations", "{iteration}", "Model calls of turns.");
    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("officina.tool.calls", "{call}", "Tool calls, by outcome.");
    private static readonly Counter<long> Handoffs = Meter.CreateCounter<long>("officina.handoffs", "{handoff}", "Turns handed off, by reason.");
    private static readonly Counter<long> Checks = Meter.CreateCounter<long>("officina.checks", "{check}", "Checks run, by whether they passed.");

    internal static Activity? StartTurn(ToolContext context) => Start($"invoke_agent {context.Agent}", ActivityKind.Internal, context, "invoke_agent");

    /// <summary>A step of the agent's pattern; the spans of its model and tool calls are its children.</summary>
    internal static Activity? StartStep(ToolContext context) => Start($"step {context.Step}", ActivityKind.Internal, context, "step");

    internal static void StepEnded(Activity? activity, string outcome) => activity?.SetTag("officina.outcome", outcome);

    /// <param name="context">Who makes the call.</param>
    /// <param name="provider">The provider, by its configured name; the Claude provider (S11) maps it to the well-known name.</param>
    /// <param name="model">The model.</param>
    internal static Activity? StartModelCall(ToolContext context, string provider, string model) =>
        Start($"chat {model}", ActivityKind.Client, context, "chat")?.SetTag("gen_ai.provider.name", provider).SetTag("gen_ai.request.model", model);

    internal static Activity? StartToolCall(ToolContext context, string tool) =>
        Start($"execute_tool {tool}", ActivityKind.Internal, context, "execute_tool")?.SetTag("gen_ai.tool.name", tool);

    internal static void ModelCallEnded(
        Activity? activity, ToolContext context, string provider, string model, StopReason stop, Usage usage, decimal cost, TimeSpan elapsed)
    {
        var input = usage.Input + usage.CacheRead + usage.CacheWrite;
        activity?.SetTag("gen_ai.response.finish_reasons", new[] { stop.ToString() })
            .SetTag("gen_ai.usage.input_tokens", input)
            .SetTag("gen_ai.usage.output_tokens", usage.Output)
            .SetTag("gen_ai.usage.cache_read.input_tokens", usage.CacheRead)
            .SetTag("gen_ai.usage.cache_write.input_tokens", usage.CacheWrite);
        var tags = new TagList
        {
            { "gen_ai.operation.name", "chat" }, { "gen_ai.provider.name", provider }, { "gen_ai.request.model", model }, { "gen_ai.agent.name", context.Agent },
        };
        if (context.TaskId is not null)
        {
            tags.Add("officina.task.id", context.TaskId); // OBS-02: tokens and cost by task
        }

        Duration.Record(elapsed.TotalSeconds, tags);
        Iterations.Add(1, tags);
        Cost.Add((double)cost, tags);
        Tokens.Record(input, [.. tags, new("gen_ai.token.type", "input")]);
        Tokens.Record(usage.Output, [.. tags, new("gen_ai.token.type", "output")]);
    }

    /// <param name="activity">The tool call's activity.</param>
    /// <param name="context">Who made the call.</param>
    /// <param name="tool">The tool.</param>
    /// <param name="outcome">The error category, or <c>ok</c>.</param>
    /// <param name="elapsed">How long the call took.</param>
    internal static void ToolCallEnded(Activity? activity, ToolContext context, string tool, string outcome, TimeSpan elapsed)
    {
        if (outcome != "ok")
        {
            activity?.SetStatus(ActivityStatusCode.Error).SetTag("error.type", outcome);
        }

        var tags = new TagList { { "gen_ai.operation.name", "execute_tool" }, { "gen_ai.tool.name", tool }, { "gen_ai.agent.name", context.Agent } };
        Duration.Record(elapsed.TotalSeconds, tags);
        ToolCalls.Add(1, [.. tags, new("officina.tool.outcome", outcome)]);
    }

    /// <summary>A step of the tool pipeline that did not allow a call (TOOL-05), as an event of the call's span.</summary>
    internal static void Decided(Activity? activity, string decidedBy, string action) =>
        activity?.AddEvent(new ActivityEvent("officina.decision", tags: new ActivityTagsCollection
        {
            ["officina.decided_by"] = decidedBy,
            ["officina.action"] = action,
        }));

    internal static void TurnEnded(Activity? activity, ToolContext context, string outcome, string? handoffReason)
    {
        activity?.SetTag("officina.outcome", outcome).SetTag("officina.handoff.reason", handoffReason);
        if (handoffReason is not null)
        {
            Handoffs.Add(1, new TagList { { "gen_ai.agent.name", context.Agent }, { "officina.handoff.reason", handoffReason } });
        }
    }

    /// <summary>An output check ran; its pass rate is the share that passed (OBS-02).</summary>
    internal static void CheckEnded(ToolContext context, string check, bool passed) =>
        Checks.Add(1, new TagList { { "gen_ai.agent.name", context.Agent }, { "officina.check.name", check }, { "officina.check.passed", passed } });

    private static Activity? Start(string name, ActivityKind kind, ToolContext context, string operation) =>
        Source.StartActivity(name, kind)?
            .SetTag("gen_ai.operation.name", operation)
            .SetTag("gen_ai.agent.name", context.Agent)
            .SetTag("officina.run.id", context.RunId)
            .SetTag("officina.step", context.Step);
}
