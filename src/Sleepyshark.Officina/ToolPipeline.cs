using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Sleepyshark.Officina;

/// <summary>
/// Runs the tool calls of one reply (ARCHITECTURE §3): for each, find the tool → validate the input → ask approval if
/// needed → record the attempt → invoke → truncate. Read calls run concurrently, a write call waits for the calls before
/// it and runs alone (TOOL-03); approvals are asked one at a time, in call order. Every call gets exactly one result,
/// in call order (CTX-06), and every failure is an error result, never an exception (TOOL-05). What happens is written
/// to <paramref name="events"/> as it happens (EVT-01). Each call that starts gets a span under <paramref name="run"/> (EVT-02).
/// Each handler gets the run's <see cref="ToolContext"/>, its memory scope included (MEM-03).
/// </summary>
internal sealed class ToolPipeline(AgentDefinition agent, AuditRecorder audit, Activity? run, ChannelWriter<RunEvent> events, string? memoryScope)
{
    /// <summary>The longest result the model gets, in characters (TOOL-06): about 16k tokens.</summary>
    internal const int MaxResultLength = 64_000;

    private readonly ToolContext context = new(memoryScope);

    public async Task<ImmutableArray<ToolResult>> RunAsync(IReadOnlyList<ToolCall> calls, CancellationToken cancellationToken)
    {
        var results = new ToolResult?[calls.Count];
        var steps = new Step?[calls.Count];
        var reads = new List<Task>();
        for (var index = 0; index < calls.Count && !cancellationToken.IsCancellationRequested; index++)
        {
            var (call, at) = (calls[index], index);
            var tool = agent.Tools.FirstOrDefault(tool => tool.Name == call.Name);
            if (tool?.Kind == ToolKind.Write)
            {
                await Task.WhenAll(reads).ConfigureAwait(false);
                reads.Clear();
            }

            var step = new Step(Telemetry.StartToolCall(agent, run, tool, call), agent.Time.GetTimestamp());
            steps[at] = step;
            events.TryWrite(new ToolCallStarted(Shown(call)));

            var (input, rejected) = await PrepareAsync(tool, call, step, cancellationToken).ConfigureAwait(false);
            if (rejected is not null)
            {
                results[at] = await EndAsync(call, step, rejected, null).ConfigureAwait(false);
                continue;
            }

            // The host may have cancelled while the approver decided or while this write waited for the reads before it.
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var running = Task.Run(async () => results[at] = await InvokeAsync(tool!, call, step, input, cancellationToken).ConfigureAwait(false), CancellationToken.None);
            if (tool!.Kind == ToolKind.Write)
            {
                await running.ConfigureAwait(false);
            }
            else
            {
                reads.Add(running);
            }
        }

        await Task.WhenAll(reads).ConfigureAwait(false);
        await ToolSources.RecordChangesAsync(agent, audit).ConfigureAwait(false);

        // Calls that never started, because the run was cancelled (AGT-05).
        for (var index = 0; index < calls.Count; index++)
        {
            results[index] ??= await EndAsync(calls[index], steps[index], new ToolOutput("The call was cancelled before it started.", true), null)
                .ConfigureAwait(false);
        }

        return [.. results.Select(result => result!)];
    }

    /// <summary>Finds, validates and approves a call: its input, or why it may not run.</summary>
    private async Task<(JsonElement Input, ToolOutput? Rejected)> PrepareAsync(Tool? tool, ToolCall call, Step step, CancellationToken cancellationToken)
    {
        if (tool is null)
        {
            return (default, new ToolOutput($"There is no tool named '{call.Name}'.", true));
        }

        JsonElement input;
        try
        {
            using var document = JsonDocument.Parse(call.Input);
            input = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return (default, new ToolOutput($"The input is not valid JSON: {exception.Message}", true));
        }

        List<string> problems;
        try
        {
            problems = SchemaValidator.Validate(tool.Schema, input);
        }
        catch (RegexMatchTimeoutException exception)
        {
            return (default, new ToolOutput($"The input could not be validated: {exception.Message}", true));
        }

        if (problems.Count > 0)
        {
            return (default, new ToolOutput($"The input does not match the tool's schema:\n{string.Join("\n", problems)}", true));
        }

        if (!tool.NeedsApprovalFor(input))
        {
            return (input, null);
        }

        if (agent.Approver is not { } approver)
        {
            return (default, new ToolOutput("The call needs approval, and this run is unattended, so it was denied.", true));
        }

        await audit.RecordAsync(AuditKind.ApprovalAsked, step.Span, tool.Name, call.Id, call.Input).ConfigureAwait(false);
        events.TryWrite(new ApprovalAsked(Shown(call)));
        var asked = agent.Time.GetTimestamp();
        Approval approval;
        try
        {
            approval = await approver.ApproveAsync(tool, call, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The approver gave no answer.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            step.Span?.SetTag("officina.tool.approval_wait", agent.Time.GetElapsedTime(asked).TotalSeconds);
            return (default, new ToolOutput("The call was cancelled while waiting for approval.", true));
        }
#pragma warning disable CA1031 // A failing approver denies the call.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            approval = Approval.Denied($"asking for approval failed: {exception.Message}");
        }

        Telemetry.Approved(step.Span, agent, call, approval.Approved, agent.Time.GetElapsedTime(asked));
        await audit.RecordAsync(AuditKind.ApprovalAnswered, step.Span, tool.Name, call.Id, outcome: approval.Approved ? "approved" : "denied", detail: approval.Reason)
            .ConfigureAwait(false);
        events.TryWrite(new ApprovalAnswered(Shown(call), approval.Approved));
        return approval.Approved
            ? (input, null)
            : (default, new ToolOutput($"The call was denied{(approval.Reason is null ? "." : $": {approval.Reason}")}", true));
    }

    private async Task<ToolResult> InvokeAsync(Tool tool, ToolCall call, Step step, JsonElement input, CancellationToken cancellationToken)
    {
        var started = agent.Time.GetTimestamp();
        if (!await audit.RecordAsync(AuditKind.ToolStarted, step.Span, tool.Name, call.Id, call.Input).ConfigureAwait(false) && tool.Kind == ToolKind.Write)
        {
            var blocked = new ToolOutput("The call was not run: its attempt could not be recorded in the audit trail.", true);
            return await EndAsync(call, step, blocked, null, blocked: true).ConfigureAwait(false);
        }

        ToolOutput output;
        try
        {
            output = await tool.Handler(input, context, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The tool returned no output.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            output = new ToolOutput("The call was cancelled while it ran.", true);
        }
#pragma warning disable CA1031 // Any failure of a tool goes back to the model as an error result (TOOL-05).
        catch (Exception exception)
#pragma warning restore CA1031
        {
            output = new ToolOutput(exception.Message, true);
        }

        return await EndAsync(call, step, output, agent.Time.GetElapsedTime(started)).ConfigureAwait(false);
    }

    /// <summary>
    /// Redacts the agent's secrets, truncates the output (TOOL-06), records the call's outcome and makes its result.
    /// <paramref name="step"/> is null for a call that never started; <paramref name="blocked"/> marks a write that did not
    /// run because its attempt could not be audited.
    /// </summary>
    private async Task<ToolResult> EndAsync(ToolCall call, Step? step, ToolOutput output, TimeSpan? duration, bool blocked = false)
    {
        var content = agent.Redact(output.Content);
        var length = content.Length;
        content = content.Length <= MaxResultLength
            ? content
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{AgentDefinition.Cut(content, MaxResultLength)}\n[Truncated: the result had {content.Length} characters; only the first {MaxResultLength} are shown.]");
        await audit.RecordAsync(AuditKind.ToolEnded, step?.Span, call.Name, call.Id, call.Input, output.IsError ? "error" : "ok", content, duration)
            .ConfigureAwait(false);
        if (step is { } started)
        {
            Telemetry.EndToolCall(
                started.Span, agent, call, started.Started, blocked ? "blocked" : output.IsError ? "error" : "ok", duration,
                length, length > MaxResultLength, content);
        }

        var result = new ToolResult(call.Id, content, output.IsError);
        events.TryWrite(new ToolCallFinished(Shown(call), result));
        return result;
    }

    /// <summary>The call as the events show it: its input without the agent's secrets (EVT-03).</summary>
    private ToolCall Shown(ToolCall call) => call with { Input = agent.Redact(call.Input) };

    /// <summary>A started call's span, if anything listens, and when it started.</summary>
    private readonly record struct Step(Activity? Span, long Started);
}
