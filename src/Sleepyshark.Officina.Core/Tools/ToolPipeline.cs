using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Observability;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Tasks;

namespace Sleepyshark.Officina.Core.Tools;

/// <summary>
/// The one path every tool call takes (TOOL-07, DESIGN.md §5): argument validation, permission rules, the gates for all
/// tools and then the tool's own, human approval, the idempotency lookup, the audit intent, the call itself with its
/// time limit and retries, and finally masking, trimming, which keeps the full result as an artifact, and the audit
/// outcome. The first step that does not allow a call decides it. Every write-tool attempt is audited, whichever step it
/// stops at (TOOL-11). Each call is published as it starts and ends, and traced (EVT-01, OBS-01).
/// </summary>
public sealed class ToolPipeline
{
    private readonly OfficinaOptions options;
    private readonly ToolCatalog catalog;
    private readonly IReadOnlyDictionary<string, IGate> gates;
    private readonly IStorage storage;
    private readonly IAuditLog audit;
    private readonly EventBus events;
    private readonly KnownSecrets secrets;
    private readonly TimeProvider time;
    private volatile PermissionMode permissionMode;

    /// <param name="options">The configuration, fixed for the pipeline's lifetime, so no agent can change its own rules (INV-10).</param>
    /// <param name="tools">
    /// The application's tools, by the id that <c>extension:&lt;id&gt;</c> sources name, and the tool servers' tools, by
    /// the <c>&lt;server&gt;/&lt;tool&gt;</c> that <c>mcp:</c> sources name.
    /// </param>
    /// <param name="gates">The application's gates, by the id that <c>extension:&lt;id&gt;</c> gates name.</param>
    /// <param name="knowledge">The application's knowledge sources, by the id that <c>extension:&lt;id&gt;</c> sources name.</param>
    /// <param name="checks">The application's checks, by the id that <c>extension:&lt;id&gt;</c> checks name.</param>
    /// <param name="storage">
    /// Where every write-tool attempt is audited, the run record and the task board are kept, and trimmed results are
    /// kept in full.
    /// </param>
    /// <param name="events">Where tool calls and approvals are published.</param>
    /// <param name="human">Who approves calls that need approval.</param>
    /// <param name="secrets">
    /// Where tools read credentials. A <see cref="KnownSecrets"/> the host shares with the providers and tool servers also
    /// removes the secrets they read (INV-06).
    /// </param>
    /// <param name="time">The clock for time limits and audit times.</param>
    /// <exception cref="ConfigurationException">The configuration has errors, including those only the tools reveal (TOOL-02).</exception>
    public ToolPipeline(
        OfficinaOptions options,
        IReadOnlyDictionary<string, ITool> tools,
        IReadOnlyDictionary<string, IGate> gates,
        IReadOnlyDictionary<string, IKnowledgeSource> knowledge,
        IReadOnlyDictionary<string, ICheck> checks,
        IStorage storage,
        EventBus events,
        IHumanChannel human,
        ISecretSource secrets,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(gates);
        ArgumentNullException.ThrowIfNull(knowledge);
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(storage);
        catalog = ToolCatalog.Create(options, tools, gates, knowledge, checks, storage.Artifacts);
        this.options = options;
        this.gates = gates;
        this.storage = storage;
        audit = storage.Audit;
        this.events = events ?? throw new ArgumentNullException(nameof(events));
        this.secrets = secrets as KnownSecrets ?? new KnownSecrets(secrets ?? throw new ArgumentNullException(nameof(secrets)));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        Owner = new OwnerChannel(human ?? throw new ArgumentNullException(nameof(human)), events, options.Run.ApprovalTimeout, time);
        permissionMode = options.Run.PermissionMode;
    }

    /// <summary>How write tool calls are decided (HITL-01). The owner may change it during the run; the next call uses it.</summary>
    public PermissionMode PermissionMode
    {
        get => permissionMode;
        set => permissionMode = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>Who answers approvals, questions and sign-offs, by their deadline.</summary>
    internal OwnerChannel Owner { get; }

    /// <summary>The tools an agent is offered, sorted by name. They never depend on the caller (TOOL-03).</summary>
    public IReadOnlyList<ToolDefinition> Offered(string agent) => catalog.Offered(agent);

    /// <summary>A run's task board, as the owner or the host changes it (TASK-08, WS-03).</summary>
    /// <exception cref="InvalidOperationException">The task board is off.</exception>
    public TaskBoard Board(string? tenant, string runId) =>
        Board(new ToolContext(runId, TaskBoard.Owner, Caller.Anonymous with { Tenant = tenant }), owner: true)
        ?? throw new InvalidOperationException("The task board is off. Set capabilities.taskBoard.enabled to true.");

    /// <summary>The run's task board as the agent of <paramref name="context"/> acts on it; null when the task board is off.</summary>
    internal TaskBoard? Board(ToolContext context, bool owner = false) =>
        options.Capabilities.TaskBoard.Enabled ? new TaskBoard(storage.Tasks, context, options, events, time, owner) : null;

    /// <summary>
    /// Runs the calls of one model reply. When every tool called is safe to run in parallel they run at the same time, up
    /// to the agent's limit; otherwise one after another. The results are in the order of the requests (LOOP-08).
    /// </summary>
    public async Task<IReadOnlyList<ToolResult>> RunAsync(ToolContext context, IReadOnlyList<ToolRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requests);
        var parallel = requests.All(request => catalog.TryGet(context.Agent, request.Name, out var tool) && tool.ParallelSafe);
        var limit = parallel ? options.Agents[context.Agent].MaxParallelToolCalls : 1;
        var results = new ToolResult[requests.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, requests.Count),
            new ParallelOptions { MaxDegreeOfParallelism = limit, CancellationToken = ct },
            async (index, token) => results[index] = await RunAsync(context, requests[index], token).ConfigureAwait(false)).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// Audits a call a provider ran itself, after the fact. Provider tools skip the per-call steps, because the provider
    /// runs them (TOOL-13).
    /// </summary>
    public async ValueTask AuditProviderToolAsync(ToolContext context, ToolRequest request, string result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (catalog.TryGet(context.Agent, request.Name, out var tool) && tool.Options.Untrusted)
        {
            context.ReadUntrusted = true; // SEC-04
        }

        // EVT-01: published like any other call, once the provider has run it.
        await events.PublishAsync(context, new ToolCallStarted(request.Name, secrets.Remove(request.Arguments.GetRawText())), ct).ConfigureAwait(false);
        var entry = Entry(context, request.Name, request.Arguments, "provider", AuditOutcome.Completed) with { Detail = secrets.Remove(result) };
        await audit.AppendAsync(context.Caller.Tenant, entry, ct).ConfigureAwait(false);
        await events.PublishAsync(context, new ToolCallEnded(request.Name, null), ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> RunAsync(ToolContext context, ToolRequest request, CancellationToken ct)
    {
        using var activity = Telemetry.StartToolCall(context, request.Name);
        var started = time.GetTimestamp();
        await events.PublishAsync(context, new ToolCallStarted(request.Name, secrets.Remove(request.Arguments.GetRawText())), ct).ConfigureAwait(false);
        var result = await DecideAndRunAsync(context, request, activity, ct).ConfigureAwait(false);
        Telemetry.ToolCallEnded(activity, context, request.Name, result.Error?.ToString() ?? "ok", time.GetElapsedTime(started));
        await events.PublishAsync(context, new ToolCallEnded(request.Name, result.Error), ct).ConfigureAwait(false);
        return result;
    }

    private async Task<ToolResult> DecideAndRunAsync(ToolContext context, ToolRequest request, Activity? activity, CancellationToken ct)
    {
        if (!catalog.TryGet(context.Agent, request.Name, out var tool) || tool.Implementation is null)
        {
            return ToolResult.Failed(ToolErrorCategory.UnknownTool);
        }

        var arguments = request.Arguments;
        var record = new RunRecord(storage.Records, context, time);
        var board = Board(context);
        var approved = false;
        for (var checking = true; checking;)
        {
            checking = false;
            await foreach (var stop in StepsAsync(context, tool, arguments, record, board, ct).ConfigureAwait(false))
            {
                if (approved && stop.Action == PolicyAction.Ask)
                {
                    // One approval answers every ask; a later deny still stops the call.
                    continue;
                }

                Telemetry.Decided(activity, stop.DecidedBy, stop.Action.ToString());
                await AuditAsync(context, tool, arguments, stop.DecidedBy, stop.Action switch
                {
                    PolicyAction.Ask => AuditOutcome.Asked,
                    PolicyAction.Route => AuditOutcome.Routed,
                    _ => AuditOutcome.Denied,
                }, ct).ConfigureAwait(false);
                if (stop.Action == PolicyAction.Route)
                {
                    return ToolResult.Routed(stop.RouteTo!, stop.Reason);
                }

                if (stop.Action == PolicyAction.Deny)
                {
                    return ToolResult.Failed(stop.Category, stop.Reason);
                }

                // LOOP-12: only this call waits; the agent continues the same turn with the answer.
                var answer = await Owner.AskAsync(
                    context, Owner.Request(context, HumanRequestKind.Approval, stop.Reason) with { Tool = tool.Name, Arguments = arguments }, ct).ConfigureAwait(false);
                if (answer is not { Approved: true })
                {
                    await AuditAsync(context, tool, arguments, "human", AuditOutcome.Denied, ct).ConfigureAwait(false);
                    return ToolResult.Failed(ToolErrorCategory.ApprovalDenied, answer is null ? Owner.NoAnswer : null);
                }

                approved = true;
                if (answer.ChangedArguments is { } changed)
                {
                    // HITL-02: a changed version goes through every step again; only a deny or a route can stop it now.
                    arguments = changed.Clone();
                    checking = true;
                    break;
                }
            }
        }

        var key = IdempotencyKey(context.RunId, tool.Name, arguments);
        if (tool.Options.Irreversible && (await audit.ReadAsync(context.Caller.Tenant, context.RunId, ct).ConfigureAwait(false))
            .Any(entry => entry.IdempotencyKey == key && entry.Outcome == AuditOutcome.Intent))
        {
            // TOOL-10, RUN-07: an earlier attempt may have taken effect, so it is never repeated; a human decides.
            await AuditAsync(context, tool, arguments, "idempotency", AuditOutcome.Routed, ct).ConfigureAwait(false);
            return ToolResult.Routed(ToolResult.Human, "an earlier attempt of this call may already have taken effect, so it is not repeated");
        }

        await AuditAsync(context, tool, arguments, approved ? "human" : null, AuditOutcome.Intent, ct).ConfigureAwait(false);
        // ING-06: only a tool configured for real values gets them, and what it returns is masked again.
        var real = tool.Options.ReceivesMaskedValues && context.Masker is not null;
        var masker = real || tool.Options.MaskResults || (tool.Options.KnowledgeSource() is { } source && options.Knowledge[source].Mask) ? context.Masker : null;

        // Output the tool streams is published with known secrets removed (INV-06), and masked like its result.
        ValueTask PublishAsync(string line, CancellationToken token) =>
            events.PublishAsync(context, new ToolOutput(tool.Name, masker?.Mask(secrets.Remove(line)) ?? secrets.Remove(line)), token);
        var (result, detail) = tool.Implementation is AskOwnerTool
            ? (await AskQuestionAsync(context, arguments, ct).ConfigureAwait(false), null)
            : await InvokeAsync(
                tool, new ToolCall(real ? context.Masker!.Restore(arguments) : arguments, context.Caller, key, secrets, record, board, PublishAsync), ct).ConfigureAwait(false);
        detail = detail is null || masker is null ? detail : masker.Mask(detail);
        if (tool.Options.Untrusted && result.Error is null)
        {
            context.ReadUntrusted = true; // only ever set, as parallel calls share the context
        }

        var outcome = result.Error is null ? AuditOutcome.Completed : AuditOutcome.Failed;
        if (detail is not null)
        {
            // TOOL-08: a read tool's details are not audited, so the log is where they are kept.
            OfficinaLog.Log.ToolFailed(context.RunId, context.Agent, context.Step ?? "", tool.Name, $"{result.Error}", detail);
        }

        await AuditAsync(context, tool, arguments, null, outcome, ct, detail).ConfigureAwait(false);

        // Secrets are removed and values masked before trimming, so a value cut in half cannot leave its start behind (INV-06).
        var content = secrets.Remove(result.Content);
        content = masker?.Mask(content) ?? content;
        result = result with { Artifacts = [.. result.Artifacts.Select(artifact => artifact with { Content = secrets.Remove(artifact.Content) })] };
        var max = tool.Options.MaxResultLength;
        if (!JsonElement.DeepEquals(arguments, request.Arguments))
        {
            content = $"[The owner changed the arguments to {arguments.GetRawText()}]\n{content}";
        }

        if (content.Length <= max)
        {
            return result with { Content = content };
        }

        var full = await storage.Artifacts.SaveAsync(context.Caller.Tenant, context.RunId, new Artifact($"{tool.Name} result", content), time.GetUtcNow(), ct)
            .ConfigureAwait(false);
        return result with { Content = $"{content[..max]}\n[Trimmed: the first {max} of {content.Length} characters. The full result is artifact {full}.]" };
    }

    /// <summary>HITL-06: the agent's question goes to the owner, and the answer is the result.</summary>
    private async Task<ToolResult> AskQuestionAsync(ToolContext context, JsonElement arguments, CancellationToken ct)
    {
        var answer = await Owner.AskAsync(context, Owner.Request(context, HumanRequestKind.Question, AskOwnerTool.Question(arguments)), ct).ConfigureAwait(false);
        return ToolResult.Success(answer is null ? $"The owner did not answer: {Owner.NoAnswer}."
            : answer is { Approved: true, Text: { } text } ? text
            : "The owner declined to answer.");
    }

    /// <summary>The checks before a call runs, in order (TOOL-05); each yields what it decides when it does not allow the call.</summary>
    private async IAsyncEnumerable<Stop> StepsAsync(
        ToolContext context, CatalogTool tool, JsonElement arguments, RunRecord record, TaskBoard? board, [EnumeratorCancellation] CancellationToken ct)
    {
        // 1. Arguments (MSG-07).
        var validation = tool.Schema!.Evaluate(arguments, new EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (!validation.IsValid)
        {
            var problem = (validation.Details ?? []).Where(detail => detail.Errors is not null)
                .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}")).FirstOrDefault();
            yield return new(PolicyAction.Deny, ToolErrorCategory.InvalidArguments, problem ?? "they do not match the input schema", "arguments");
            yield break;
        }

        // 2. Permissions: the caller's, narrowed by the agent's (INV-02), then the permission rules, where the first match decides.
        var held = context.Caller.Id is null ? options.Policies.AnonymousPermissions : (IEnumerable<string>)context.Caller.Permissions;
        var narrowed = options.Agents[context.Agent].Permissions;
        if (tool.Options.Permissions.FirstOrDefault(needed => !held.Contains(needed) || narrowed?.Contains(needed) == false) is { } missing)
        {
            yield return new(PolicyAction.Deny, ToolErrorCategory.NotAuthorised, $"the caller does not hold the permission {missing}", "permissions");
        }

        var allowedByRule = false;
        foreach (var (index, rule) in options.Policies.PermissionRules.Index())
        {
            if (rule.Tool == tool.Name && (rule.When?.Holds(arguments) ?? true))
            {
                if (rule.Action != PolicyAction.Allow)
                {
                    yield return new(rule.Action, ToolErrorCategory.NotAuthorised, rule.Reason ?? "a permission rule", $"policies.permissionRules[{index}]", rule.To);
                }

                allowedByRule = rule.Action == PolicyAction.Allow;
                break;
            }
        }

        // HITL-01: the mode is read for each call, so a change applies to the next one.
        var mode = PermissionMode;
        if (tool.Kind == ToolKind.Write && mode == PermissionMode.ReadOnly)
        {
            yield return new(PolicyAction.Deny, ToolErrorCategory.NotAuthorised, "the run is read-only", "permissionMode");
        }
        else if (tool.Kind == ToolKind.Write && mode == PermissionMode.Ask && !allowedByRule)
        {
            yield return new(PolicyAction.Ask, ToolErrorCategory.PolicyViolation, "no permission rule allows this call", "permissionMode");
        }

        // 3. Gates for all tools, then the tool's own.
        foreach (var name in options.Policies.Gates.Concat(tool.Options.Gates))
        {
            var gate = options.Gates[name];
            var decision = gate.ExtensionId() is { } id
                ? await gates[id].EvaluateAsync(new GateContext(context.Agent, tool.Name, arguments, context.Caller, context.ReadUntrusted, record, board), ct).ConfigureAwait(false)
                : gate.When?.Holds(arguments) == false || (gate.Use == GateOptions.UntrustedContentApproval && !context.ReadUntrusted) ? GateDecision.Allow
                : gate.Use == GateOptions.Deny ? GateDecision.Deny($"gate {name}")
                : GateDecision.Ask($"gate {name}");
            if (decision.Action != PolicyAction.Allow)
            {
                yield return new(decision.Action, ToolErrorCategory.PolicyViolation, decision.Reason ?? $"gate {name}", $"gates.{name}", decision.RouteTo);
            }
        }

        // 4. Human approval.
        if (tool.Approval == Approval.Always)
        {
            yield return new(PolicyAction.Ask, ToolErrorCategory.PolicyViolation, "the tool needs approval", "approval");
        }
        else if (tool.Options.Irreversible && options.Capabilities.HumanInteraction.SignsOff(SignOff.IrreversibleAction))
        {
            yield return new(PolicyAction.Ask, ToolErrorCategory.PolicyViolation, "an irreversible action needs the owner's sign-off", "signOffs");
        }
    }

    /// <summary>Runs the tool with its time limit, retrying timeouts and unavailable errors, never for irreversible tools (TOOL-04).</summary>
    private async Task<(ToolResult Result, string? Detail)> InvokeAsync(CatalogTool tool, ToolCall call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = new CancellationTokenSource(tool.Options.Timeout, time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            (ToolResult Result, string? Detail) outcome;
            try
            {
                // Waiting on the token too ends the call at its time limit even if the tool ignores cancellation.
                outcome = (await tool.Implementation!.InvokeAsync(call, stop.Token).AsTask().WaitAsync(stop.Token).ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                outcome = (ToolResult.Failed(ToolErrorCategory.Timeout), $"no result within {tool.Options.Timeout}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // TOOL-08: internal details go to the audit log, never to the model.
                outcome = (ToolResult.Failed(ToolErrorCategory.Failed), secrets.Remove($"{exception.GetType().Name}: {exception.Message}"));
            }

            if (!outcome.Result.Retryable || tool.Options.Irreversible || attempt >= tool.Options.MaxAttempts)
            {
                return outcome;
            }
        }
    }

    /// <summary>Records a step of a write-tool attempt (TOOL-11, INV-05); a read tool's calls are not audited.</summary>
    private ValueTask AuditAsync(
        ToolContext context, CatalogTool tool, JsonElement arguments, string? decidedBy, AuditOutcome outcome, CancellationToken ct, string? detail = null) =>
        tool.Kind == ToolKind.Write
            ? audit.AppendAsync(context.Caller.Tenant, Entry(context, tool.Name, arguments, decidedBy, outcome) with { Detail = detail }, ct)
            : ValueTask.CompletedTask;

    private AuditEntry Entry(ToolContext context, string tool, JsonElement arguments, string? decidedBy, AuditOutcome outcome) =>
        new(context.RunId, context.Agent, context.Caller.Id, tool, secrets.Remove(arguments.GetRawText()), decidedBy, outcome,
            time.GetUtcNow(), IdempotencyKey(context.RunId, tool, arguments));

    /// <summary>The same for the same run, tool and arguments, whatever the order of the arguments' properties (TOOL-10).</summary>
    private static string IdempotencyKey(string runId, string tool, JsonElement arguments)
    {
        var text = $"{runId}\n{tool}\n{Canonical(arguments)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };

    /// <summary>What a step decided when it did not allow the call.</summary>
    private sealed record Stop(PolicyAction Action, ToolErrorCategory Category, string Reason, string DecidedBy, string? RouteTo = null);
}
