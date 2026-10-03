using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Tools;

/// <summary>The steps of DESIGN.md §5, a test or more per row, in order; the first step that does not allow a call decides it (TOOL-05, TOOL-07).</summary>
public class ToolPipelineTests
{
    private const string IssueSchema = """{ "type": "object", "properties": { "title": { "type": "string" }, "branch": { "type": "string" } }, "required": ["title"] }""";

    private readonly ToolSetup setup = new();
    private readonly FakeTool createIssue = new(ToolKind.Write, IssueSchema);

    public ToolPipelineTests() => setup.Tools["create_issue"] = createIssue;

    private static ToolOptions CreateIssue => Extension("create_issue") with { GateExemption = "Tests only." };

    // Row 1: validate arguments (MSG-07).
    [Fact]
    public async Task Arguments_that_do_not_match_the_input_schema_are_invalid_and_the_tool_does_not_run()
    {
        var pipeline = setup.Create(Options(("create_issue", CreateIssue)));

        var result = await RunAsync(pipeline, "create_issue", """{ "body": "no title" }""");

        Assert.Equal(ToolErrorCategory.InvalidArguments, result.Error);
        Assert.StartsWith("invalid arguments: ", result.Content, StringComparison.Ordinal);
        Assert.Empty(createIssue.Calls);
        Assert.Equal(("arguments", AuditOutcome.Denied), Single(setup.Audit));
    }

    // Row 2: permission rules (TOOL-03, INV-02, INV-03, ING-05).
    [Fact]
    public async Task A_caller_without_the_tools_permission_is_not_authorised_though_the_tool_is_offered()
    {
        var options = Options(("create_issue", CreateIssue with { Permissions = ["issues:admin"] }));
        var pipeline = setup.Create(options);

        // Arguments cannot grant permissions or change the caller (INV-03).
        var result = await RunAsync(pipeline, "create_issue", """{ "title": "x", "caller": "admin", "permissions": ["issues:admin"] }""");

        Assert.Equal(("create_issue", ToolErrorCategory.NotAuthorised), (Assert.Single(pipeline.Offered(Agent)).Name, result.Error));
        Assert.Equal("not authorised: the caller does not hold the permission issues:admin", result.Content);
        Assert.Equal(("permissions", AuditOutcome.Denied), Single(setup.Audit));
    }

    [Fact]
    public async Task The_agent_narrows_the_callers_permissions()
    {
        var options = Options(("create_issue", CreateIssue with { Permissions = ["issues:write"] }));
        options = options with { Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Permissions = [] } } };

        Assert.Equal(ToolErrorCategory.NotAuthorised, (await RunAsync(setup.Create(options), "create_issue", """{ "title": "x" }""")).Error);
    }

    [Fact]
    public async Task An_anonymous_caller_holds_only_the_permissions_configured_for_anonymous_callers()
    {
        var options = Options(("create_issue", CreateIssue with { Permissions = ["issues:write"] }));
        var anonymous = Context with { Caller = Caller.Anonymous with { Permissions = new HashSet<string> { "issues:write" } } };

        var denied = await RunAsync(setup.Create(options), "create_issue", """{ "title": "x" }""", anonymous);
        var allowed = await RunAsync(
            setup.Create(options with { Policies = new() { AnonymousPermissions = ["issues:write"] } }), "create_issue", """{ "title": "x" }""", Context with { Caller = Caller.Anonymous });

        Assert.Equal((ToolErrorCategory.NotAuthorised, (ToolErrorCategory?)null), (denied.Error, allowed.Error));
    }

    [Fact]
    public async Task The_first_matching_permission_rule_decides()
    {
        var pipeline = setup.Create(Options(("create_issue", CreateIssue)) with
        {
            Policies = new()
            {
                PermissionRules =
                [
                    new() { Tool = "create_issue", When = new() { Field = "args.branch", Is = "dev" }, Action = PolicyAction.Allow },
                    new() { Tool = "create_issue", Action = PolicyAction.Deny, Reason = "Issues are filed from dev only." },
                ],
            },
        });

        var onDev = await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "dev" }""");
        var onMain = await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "main" }""");

        Assert.Null(onDev.Error);
        Assert.Equal((ToolErrorCategory.NotAuthorised, "not authorised: Issues are filed from dev only."), (onMain.Error, onMain.Content));
        Assert.Equal("policies.permissionRules[1]", setup.Audit.Entries[^1].DecidedBy);
    }

    [Fact]
    public async Task A_permission_rule_can_route_the_call_to_another_agent()
    {
        var options = Options(("create_issue", CreateIssue));
        options = options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents) { ["lead"] = new() { Instructions = "Lead." } },
            Policies = new() { PermissionRules = [new() { Tool = "create_issue", Action = PolicyAction.Route, To = "lead", Reason = "The lead files issues." }] },
        };

        var result = await RunAsync(setup.Create(options), "create_issue", """{ "title": "x" }""");

        Assert.Equal(("lead", "routed to lead: The lead files issues."), (result.RouteTo, result.Content));
        Assert.Empty(createIssue.Calls);
        Assert.Equal(("policies.permissionRules[0]", AuditOutcome.Routed), Single(setup.Audit));
    }

    // Row 3: gates for all tools, then the tool's own (TOOL-05, TOOL-06).
    [Fact]
    public async Task Gates_for_all_tools_run_before_the_tools_own_and_the_first_that_does_not_allow_decides()
    {
        var seen = new List<string>();
        var gate = new RecordingGate("everyone", seen, GateDecision.Allow);
        setup.Gates["Everyone"] = gate;
        setup.Gates["Dedupe"] = new RecordingGate("dedupe", seen, GateDecision.Deny("A matching issue is open."));
        setup.Gates["Never"] = new RecordingGate("never", seen, GateDecision.Allow);
        var pipeline = setup.Create(Options(("create_issue", CreateIssue with { Gates = ["dedupe", "never"] })) with
        {
            Gates = new Dictionary<string, GateOptions>
            {
                ["everyone"] = new() { Use = "extension:Everyone" },
                ["dedupe"] = new() { Use = "extension:Dedupe" },
                ["never"] = new() { Use = "extension:Never" },
            },
            Policies = new() { Gates = ["everyone"] },
        });

        var result = await RunAsync(pipeline, "create_issue", """{ "title": "x" }""");

        Assert.Equal(["everyone", "dedupe"], seen);
        Assert.Equal((Agent, "create_issue", Owner), (gate.Contexts[0].Agent, gate.Contexts[0].Tool, gate.Contexts[0].Caller));
        Assert.Equal((ToolErrorCategory.PolicyViolation, "policy violation: A matching issue is open."), (result.Error, result.Content));
        Assert.Equal(("gates.dedupe", AuditOutcome.Denied), Single(setup.Audit));
    }

    [Fact]
    public async Task The_built_in_deny_gate_acts_only_when_its_condition_holds()
    {
        var pipeline = setup.Create(Options(("create_issue", Extension("create_issue") with { Gates = ["no-main"] })) with
        {
            Gates = new Dictionary<string, GateOptions>
            {
                ["no-main"] = new() { Use = GateOptions.Deny, When = new() { Field = "args.branch", In = ["main", "master"] } },
            },
        });

        var onDev = await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "dev" }""");
        var onMain = await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "main" }""");

        Assert.Equal(((ToolErrorCategory?)null, ToolErrorCategory.PolicyViolation), (onDev.Error, onMain.Error));
        Assert.Single(createIssue.Calls);
    }

    // Row 4: human approval (TOOL-04).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_approved_call_runs_and_the_approval_is_audited(bool irreversibleByDefault)
    {
        setup.Human.Answer(HumanAnswer.Approve);
        var tool = irreversibleByDefault ? CreateIssue with { Irreversible = true } : CreateIssue with { Approval = Approval.Always };
        var pipeline = setup.Create(Options(("create_issue", tool)));

        var result = await RunAsync(pipeline, "create_issue", """{ "title": "x" }""");

        Assert.Equal("ok", result.Content);
        Assert.Equal(("create_issue", "the tool needs approval"), (Assert.Single(setup.Human.Requests).Tool, setup.Human.Requests[0].Summary));
        Assert.Equal(irreversibleByDefault, setup.Human.Requests[0].Irreversible); // so the owner names it to approve it
        Assert.Equal(
            [("approval", AuditOutcome.Asked), ("human", AuditOutcome.Intent), (null, AuditOutcome.Completed)],
            setup.Audit.Entries.Select(entry => (entry.DecidedBy, entry.Outcome)));
    }

    [Fact]
    public async Task A_denied_approval_does_not_run_the_call()
    {
        setup.Human.Answer(HumanAnswer.Deny);
        var pipeline = setup.Create(Options(("create_issue", CreateIssue with { Approval = Approval.Always })));

        var result = await RunAsync(pipeline, "create_issue", """{ "title": "x" }""");

        Assert.Equal((ToolErrorCategory.ApprovalDenied, "approval denied"), (result.Error, result.Content));
        Assert.Empty(createIssue.Calls);
        Assert.Equal(("human", AuditOutcome.Denied), (setup.Audit.Entries[^1].DecidedBy, setup.Audit.Entries[^1].Outcome));

        // EVT-01.
        Assert.Equal(
            [new ToolCallStarted("create_issue", """{ "title": "x" }"""), new HumanAsked(HumanRequestKind.Approval, "the tool needs approval", "create_issue"),
                new HumanAnswered(HumanRequestKind.Approval, false, false, "create_issue"), new ToolCallEnded("create_issue", ToolErrorCategory.ApprovalDenied, "approval denied")],
            (await setup.Events.ReadAsync("acme", "run-1", 0, TestContext.Current.CancellationToken)).Select(read => read.Payload));
    }

    [Fact]
    public async Task Approval_by_rule_asks_only_when_the_gates_condition_holds()
    {
        setup.Human.Answer(HumanAnswer.Approve);
        var pipeline = setup.Create(Options(("create_issue", Extension("create_issue") with { Gates = ["main-approval"] })) with
        {
            Gates = new Dictionary<string, GateOptions>
            {
                ["main-approval"] = new() { Use = GateOptions.RequireApproval, When = new() { Field = "args.branch", Is = "main" } },
            },
        });

        await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "dev" }""");
        await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "main" }""");

        Assert.Equal("gate main-approval", Assert.Single(setup.Human.Requests).Summary);
        Assert.Equal(2, createIssue.Calls.Count);
    }

    // Row 5: idempotency lookup, irreversible tools only (TOOL-10).
    [Fact]
    public async Task An_irreversible_call_is_carried_out_at_most_once_for_the_same_arguments_and_then_goes_to_a_human()
    {
        var pipeline = setup.Create(Options(("create_issue", CreateIssue with { Irreversible = true, Approval = Approval.Never })));

        var first = await RunAsync(pipeline, "create_issue", """{ "title": "x", "branch": "dev" }""");
        var again = await RunAsync(pipeline, "create_issue", """{ "branch": "dev", "title": "x" }""");
        var other = await RunAsync(pipeline, "create_issue", """{ "title": "y" }""");

        Assert.Equal(("ok", ToolResult.Human, "ok"), (first.Content, again.RouteTo, other.Content));
        Assert.Equal(2, createIssue.Calls.Count);
        Assert.Equal(("idempotency", AuditOutcome.Routed), (setup.Audit.Entries[2].DecidedBy, setup.Audit.Entries[2].Outcome));
    }

    [Fact]
    public async Task Identical_irreversible_calls_in_one_reply_run_once_even_if_the_tool_declares_itself_parallel_safe()
    {
        var pay = new FakeTool(ToolKind.Write, parallelSafe: true);
        setup.Tools["pay"] = pay;
        var pipeline = setup.Create(Options(("pay", Extension("pay") with { GateExemption = "Tests only.", Irreversible = true, Approval = Approval.Never })));
        var request = new ToolRequest("pay", Args("""{ "amount": 5 }"""));

        var results = await pipeline.RunAsync(Context, [request, request], TestContext.Current.CancellationToken);

        Assert.Single(pay.Calls);
        Assert.Equal(ToolResult.Human, results[1].RouteTo);
    }

    [Fact]
    public async Task An_intent_without_an_outcome_is_never_run_again_after_a_restart()
    {
        using var crash = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var hanging = new FakeTool(ToolKind.Write, run: async (_, ct) =>
        {
            await crash.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("never");
        });
        setup.Tools["create_issue"] = hanging;
        var options = Options(("create_issue", CreateIssue with { Irreversible = true, Approval = Approval.Never }));
        var request = new ToolRequest("create_issue", Args("""{ "title": "x" }"""));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Create(options).RunAsync(Context, [request], crash.Token));
        var afterRestart = Assert.Single(await setup.Create(options).RunAsync(Context, [request], TestContext.Current.CancellationToken));

        Assert.Equal(ToolResult.Human, afterRestart.RouteTo);
        Assert.Single(hanging.Calls);
        Assert.Equal([AuditOutcome.Intent, AuditOutcome.Routed], setup.Audit.Entries.Select(entry => entry.Outcome));
    }

    // INV-05, REL-03; row 6: the intent is audited, durably, before the tool runs.
    [Fact]
    public async Task The_intent_is_audited_before_the_tool_runs_as_the_caller_with_the_idempotency_key()
    {
        AuditEntry[] before = [];
        setup.Tools["create_issue"] = new FakeTool(ToolKind.Write, run: (_, _) =>
        {
            before = [.. setup.Audit.Entries];
            return ValueTask.FromResult(ToolResult.Success("ok"));
        });
        var pipeline = setup.Create(Options(("create_issue", CreateIssue)));

        await RunAsync(pipeline, "create_issue", """{ "title": "x" }""");

        var intent = Assert.Single(before);
        var call = Assert.Single(((FakeTool)setup.Tools["create_issue"]).Calls);
        Assert.Equal((AuditOutcome.Intent, call.IdempotencyKey, Owner), (intent.Outcome, intent.IdempotencyKey, call.Caller));
        Assert.Equal(("run-1", Agent, "owner", "create_issue", """{ "title": "x" }""", setup.Time.GetUtcNow()),
            (intent.RunId, intent.Agent, intent.Caller, intent.Tool, intent.Arguments, intent.Time));
    }

    // Row 7: run as the caller, with a time limit and retries (TOOL-04, TOOL-08, INV-02).
    [Fact]
    public async Task A_call_that_takes_longer_than_its_time_limit_times_out_and_may_be_retried()
    {
        var started = new TaskCompletionSource();
        setup.Tools["create_issue"] = new FakeTool(ToolKind.Write, run: async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ToolResult.Success("never");
        });
        var pipeline = setup.Create(Options(("create_issue", CreateIssue with { Timeout = TimeSpan.FromSeconds(30) })));

        var running = RunAsync(pipeline, "create_issue", """{ "title": "x" }""");
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        setup.Time.Advance(TimeSpan.FromSeconds(30));
        var result = await running;

        Assert.Equal((ToolErrorCategory.Timeout, "timed out", true), (result.Error, result.Content, result.Retryable));
        Assert.Equal((AuditOutcome.Failed, "no result within 00:00:30"), (setup.Audit.Entries[^1].Outcome, setup.Audit.Entries[^1].Detail));
    }

    [Theory]
    [InlineData(false, 2, "ok")]
    [InlineData(true, 1, "unavailable")]
    public async Task Unavailable_errors_are_retried_except_for_irreversible_tools(bool irreversible, int calls, string content)
    {
        var attempts = 0;
        var tool = new FakeTool(ToolKind.Write, run: (_, _) => ValueTask.FromResult(
            ++attempts == 1 ? ToolResult.Failed(ToolErrorCategory.Unavailable) : ToolResult.Success("ok")));
        setup.Tools["create_issue"] = tool;
        var pipeline = setup.Create(Options(("create_issue", CreateIssue with { MaxAttempts = 3, Irreversible = irreversible, Approval = Approval.Never })));

        var result = await RunAsync(pipeline, "create_issue", """{ "title": "x" }""");

        Assert.Equal((calls, content), (tool.Calls.Count, result.Content));
    }

    // Row 8: trim the result, audit the outcome (TOOL-09, TOOL-11).
    [Fact]
    public async Task A_long_result_is_trimmed_before_it_enters_the_conversation_and_kept_in_full_to_page_through()
    {
        setup.Tools["read_log"] = new FakeTool(ToolKind.Read, run: (_, _) => ValueTask.FromResult(ToolResult.Success(new string('a', 40) + "end")));
        var pipeline = setup.Create(Options(
            ("read_log", Extension("read_log") with { MaxResultLength = 10 }), ("page", new() { Source = "builtin:artifact.page" })));

        var result = await RunAsync(pipeline, "read_log");
        var page = await RunAsync(pipeline, "page", """{ "artifact": 1, "offset": 38 }""");

        Assert.Equal("aaaaaaaaaa\n[Trimmed: the first 10 of 43 characters. The full result is artifact 1.]", result.Content);
        Assert.Equal("aaend\n[Characters 38 to 43 of 43.]", page.Content);
        Assert.Equal(ToolErrorCategory.InvalidArguments, (await RunAsync(pipeline, "page", """{ "artifact": 1 }""", Context with { RunId = "run-2" })).Error);
        Assert.Empty(setup.Audit.Entries);
    }

    [Fact]
    public async Task A_secret_cut_by_trimming_leaves_no_part_of_itself()
    {
        setup.Secrets["TOKEN"] = "tok-s3cr3t";
        setup.Tools["read_log"] = new FakeTool(ToolKind.Read, run: async (call, ct) =>
            ToolResult.Success($"auth {await call.Secrets.GetAsync("TOKEN", ct)} done"));
        var pipeline = setup.Create(Options(("read_log", Extension("read_log") with { MaxResultLength = 9 })));

        var result = await RunAsync(pipeline, "read_log");

        Assert.Equal("auth [sec\n[Trimmed: the first 9 of 18 characters. The full result is artifact 1.]", result.Content);
        Assert.Equal("auth [secret] done", (await setup.Storage.Artifacts.ReadAsync("acme", "run-1", 1, TestContext.Current.CancellationToken))!.Content);
    }

    // TOOL-08, TEST-13, INV-06, SEC-05.
    [Fact]
    public async Task A_failure_reaches_the_model_as_a_category_only_and_its_details_go_to_the_audit_log_without_secrets()
    {
        setup.Secrets["TRACKER_TOKEN"] = "tok-s3cr3t";
        setup.Tools["create_issue"] = new FakeTool(ToolKind.Write, run: async (call, ct) =>
        {
            var token = await call.Secrets.GetAsync("TRACKER_TOKEN", ct);
            throw new HttpRequestException($"POST https://tracker.example.com failed with Authorization: Bearer {token}");
        });
        var pipeline = setup.Create(Options(("create_issue", CreateIssue)));

        var result = await RunAsync(pipeline, "create_issue", """{ "title": "x" }""");

        Assert.Equal((ToolErrorCategory.Failed, "failed", false), (result.Error, result.Content, result.Retryable));
        Assert.Equal("HttpRequestException: POST https://tracker.example.com failed with Authorization: Bearer [secret]", setup.Audit.Entries[^1].Detail);

        // EVT-01: the owner sees why, on one line: what the model read, then the detail it did not.
        Assert.Equal(
            "failed (HttpRequestException: POST https://tracker.example.com failed with Authorization: Bearer [secret])",
            (await setup.Events.ReadAsync("acme", "run-1", 0, TestContext.Current.CancellationToken)).Select(read => read.Payload).OfType<ToolCallEnded>().Single().Reason);
    }

    [Fact]
    public async Task A_secret_the_tool_read_never_reaches_the_model_the_audit_log_or_events()
    {
        setup.Secrets["TRACKER_TOKEN"] = "tok-s3cr3t";
        setup.Tools["create_issue"] = new FakeTool(ToolKind.Write, run: async (call, ct) =>
            ToolResult.Success($"created with {await call.Secrets.GetAsync("TRACKER_TOKEN", ct)}"));
        var pipeline = setup.Create(Options(("create_issue", CreateIssue)));
        await RunAsync(pipeline, "create_issue", """{ "title": "first" }""");

        var echoed = await RunAsync(pipeline, "create_issue", """{ "title": "tok-s3cr3t" }""");

        Assert.Equal("created with [secret]", echoed.Content);
        Assert.DoesNotContain(setup.Audit.Entries, entry => $"{entry}".Contains("tok-s3cr3t", StringComparison.Ordinal));
        Assert.DoesNotContain(
            await setup.Events.ReadAsync("acme", "run-1", 0, TestContext.Current.CancellationToken),
            read => $"{read.Payload}".Contains("tok-s3cr3t", StringComparison.Ordinal));
    }

    // LOOP-08.
    [Fact]
    public async Task Calls_that_are_all_safe_run_in_parallel_and_their_results_come_back_in_the_order_requested()
    {
        var bothStarted = new TaskCompletionSource();
        var started = 0;
        async ValueTask<ToolResult> Meet(string name, CancellationToken ct)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.SetResult();
            }

            // Each call waits for the other, so this finishes only if they run at the same time.
            await bothStarted.Task.WaitAsync(ct);
            return ToolResult.Success(name);
        }

        setup.Tools["slow"] = new FakeTool(ToolKind.Read, parallelSafe: true, run: async (_, ct) => { await Task.Yield(); return await Meet("slow", ct); });
        setup.Tools["fast"] = new FakeTool(ToolKind.Read, parallelSafe: true, run: (_, ct) => Meet("fast", ct));
        var pipeline = setup.Create(Options(("slow", Extension("slow")), ("fast", Extension("fast"))));

        var results = await pipeline.RunAsync(
            Context, [new ToolRequest("slow", Args("{}")), new ToolRequest("fast", Args("{}"))], TestContext.Current.CancellationToken);

        Assert.Equal(["slow", "fast"], results.Select(result => result.Content));
    }

    [Fact]
    public async Task Calls_run_one_at_a_time_when_any_tool_is_not_safe_to_run_in_parallel()
    {
        var running = 0;
        var most = 0;
        async ValueTask<ToolResult> Track(string name)
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Yield();
            Interlocked.Decrement(ref running);
            return ToolResult.Success(name);
        }

        setup.Tools["safe"] = new FakeTool(ToolKind.Read, parallelSafe: true, run: (_, _) => Track("safe"));
        setup.Tools["unsafe"] = new FakeTool(ToolKind.Read, run: (_, _) => Track("unsafe"));
        var pipeline = setup.Create(Options(("safe", Extension("safe")), ("unsafe", Extension("unsafe"))));

        var results = await pipeline.RunAsync(
            Context, [new ToolRequest("unsafe", Args("{}")), new ToolRequest("safe", Args("{}")), new ToolRequest("unsafe", Args("{}"))],
            TestContext.Current.CancellationToken);

        Assert.Equal(1, most);
        Assert.Equal(["unsafe", "safe", "unsafe"], results.Select(result => result.Content));
    }

    // TOOL-13.
    [Fact]
    public async Task A_provider_tool_is_offered_only_when_configured_and_is_audited_after_the_fact()
    {
        var options = Options(("create_issue", CreateIssue), ("web_search", new() { Source = "provider:web_search", Reason = "Research." }));
        var pipeline = setup.Create(options);

        await pipeline.AuditProviderToolAsync(Context, new ToolRequest("web_search", Args("""{ "query": "xunit" }""")), "3 results", TestContext.Current.CancellationToken);

        Assert.Equal(
            [("create_issue", null), ("web_search", "web_search")],
            pipeline.Offered(Agent).Select(tool => (tool.Name, tool.ProviderTool)));
        var entry = Assert.Single(setup.Audit.Entries);
        Assert.Equal(("web_search", "provider", AuditOutcome.Completed, "3 results"), (entry.Tool, entry.DecidedBy, entry.Outcome, entry.Detail));
        Assert.Equal(ToolErrorCategory.UnknownTool, (await RunAsync(pipeline, "web_search")).Error);
        Assert.Equal("unknown tool", (await RunAsync(pipeline, "delete_repo")).Content);
    }

    // INV-10: a tool or gate receives no configuration, so it cannot change its own rules.
    [Fact]
    public void Tools_and_gates_receive_nothing_from_the_configuration()
    {
        var received = typeof(ToolCall).GetProperties().Concat(typeof(GateContext).GetProperties()).Select(property => property.PropertyType);

        Assert.DoesNotContain(received, type => type.Namespace == typeof(OfficinaOptions).Namespace);
    }

    private static (string?, AuditOutcome) Single(InMemoryAuditLog audit)
    {
        var entry = Assert.Single(audit.Entries);
        return (entry.DecidedBy, entry.Outcome);
    }

    private sealed class RecordingGate(string name, List<string> seen, GateDecision decision) : IGate
    {
        public List<GateContext> Contexts { get; } = [];

        public ValueTask<GateDecision> EvaluateAsync(GateContext context, CancellationToken ct)
        {
            seen.Add(name);
            Contexts.Add(context);
            return ValueTask.FromResult(decision);
        }
    }
}
