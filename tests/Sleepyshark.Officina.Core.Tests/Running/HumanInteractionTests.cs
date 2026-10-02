using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tests.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>
/// The owner in the loop (HITL): permission modes, approvals and their deadline, questions, sign-offs, and pausing and
/// cancelling agents (RUN-06), with TEST-27. The agent <c>dev</c> is offered <c>edit</c>, which writes, <c>read</c> and
/// <c>ask</c>; the agent <c>other</c> has no tools.
/// </summary>
public class HumanInteractionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTool edit = new(ToolKind.Write);

    // HITL-01.
    [Fact]
    public async Task Each_permission_mode_decides_write_calls_and_the_mode_can_change_during_the_run()
    {
        TestKit? kit = null;
        var edits = 0;
        var tools = new Dictionary<string, ITool>
        {
            // The owner changes the mode while the turn runs; the next call uses it.
            ["edit"] = new FakeTool(ToolKind.Write, run: (_, _) => Changed(++edits == 1 ? PermissionMode.ReadOnly : PermissionMode.Auto)),
            ["read"] = new FakeTool(ToolKind.Read, run: (_, _) => Changed(PermissionMode.Auto)),
        };
        var options = Options(
            ("edit", Extension("edit") with { GateExemption = "Tests only." }), ("log", Extension("log") with { GateExemption = "Tests only." }), ("read", Extension("read"))) with
        {
            Run = new() { PermissionMode = PermissionMode.Ask },
            Policies = new() { PermissionRules = [new() { Tool = "log", Action = PolicyAction.Allow }] },
        };
        kit = new TestKit(options, new Dictionary<string, ITool>(tools) { ["log"] = new FakeTool(ToolKind.Write) });
        kit.Human.Answer(HumanAnswer.Approve);
        kit.Model.CallTools(("edit", "{}"), ("log", "{}")) // ask: edit is asked about, log is allowed by a rule
            .CallTools(("edit", "{}"), ("read", "{}")) // readOnly: edit is denied
            .CallTools(("edit", "{}")) // auto: edit runs without asking
            .Reply("Done.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, 2), (result.Outcome, edits));
        Assert.Equal("no permission rule allows this call", Assert.Single(kit.Human.Requests).Summary);
        Assert.Contains(result.Transcript.SelectMany(message => message.Content).OfType<ToolResultContent>(), content => content.Text.Contains("not authorised: the run is read-only"));

        ValueTask<ToolResult> Changed(PermissionMode mode)
        {
            kit!.Runner.PermissionMode = mode;
            return ValueTask.FromResult(ToolResult.Success("ok"));
        }
    }

    // HITL-02.
    [Fact]
    public async Task A_changed_version_goes_through_the_checks_again_before_it_runs()
    {
        var setup = new ToolSetup();
        var edit = new FakeTool(ToolKind.Write, """{ "type": "object", "properties": { "path": { "type": "string" } } }""");
        setup.Tools["edit"] = edit;
        var pipeline = setup.Create(Options(("edit", Extension("edit") with { GateExemption = "Tests only.", Approval = Approval.Always })) with
        {
            Policies = new() { PermissionRules = [new() { Tool = "edit", When = new() { Field = "args.path", Is = "secrets.txt" }, Reason = "Never." }] },
        });
        setup.Human.Answer(HumanAnswer.ApproveChanged(Args("""{ "path": "secrets.txt" }"""))).Answer(HumanAnswer.ApproveChanged(Args("""{ "path": "b.txt" }""")));

        var denied = await RunAsync(pipeline, "edit", """{ "path": "a.txt" }""");
        var changed = await RunAsync(pipeline, "edit", """{ "path": "a.txt" }""");

        Assert.Equal("not authorised: Never.", denied.Content);
        Assert.Equal("[The owner changed the arguments to { \"path\": \"b.txt\" }]\nok", changed.Content);
        Assert.Equal("b.txt", Assert.Single(edit.Calls).Arguments.GetProperty("path").GetString());
        Assert.Equal(2, setup.Human.Requests.Count); // the owner's own change is not asked about again
    }

    // HITL-02, EGR-03.
    [Fact]
    public async Task With_no_answer_by_the_deadline_the_approval_is_denied_and_the_turn_hands_off()
    {
        var human = new WaitingHuman();
        var kit = Kit(human);
        kit.Model.CallTools(("edit", "{}"));

        var run = kit.RunAsync(Agent, "work", Ct);
        var (request, _) = await human.NextAsync(Ct);
        kit.Time.Advance(TimeSpan.FromMinutes(30));
        var result = await run;

        Assert.Equal((HumanRequestKind.Approval, "edit", kit.Time.GetUtcNow()), (request.Kind, request.Tool, request.Deadline));
        Assert.Equal(HandoffReason.ApprovalDeniedOrTimedOut, result.Handoff!.Reason);
        Assert.Equal("approval denied: no answer within 00:30:00", Assert.Single(result.Handoff.ToolCalls).Result.Content);
        Assert.Empty(edit.Calls);
    }

    // LOOP-12, HITL-05, TEST-27.
    [Fact]
    public async Task An_approval_pauses_only_the_waiting_agent_which_then_continues_the_same_turn()
    {
        var human = new WaitingHuman();
        var kit = Kit(human);
        kit.Model.CallTools(("edit", "{}")).Reply("Other done.").Reply("Dev done.");

        var dev = kit.RunAsync(Agent, "work", Ct);
        var (_, answer) = await human.NextAsync(Ct);
        var other = await kit.RunAsync("other", "work", Ct);

        Assert.Equal((AgentOutcome.Completed, false), (other.Outcome, dev.IsCompleted));
        answer(HumanAnswer.Approve);
        var result = await dev;
        Assert.Equal((AgentOutcome.Completed, "Dev done.", 2), (result.Outcome, result.Output, result.Statistics.Iterations));
        Assert.Single(edit.Calls);
    }

    // HITL-06.
    [Fact]
    public async Task An_agent_asks_the_owner_a_question_and_continues_the_same_turn_with_the_answer()
    {
        var kit = Kit();
        kit.Human.Answer(HumanAnswer.Reply("Use Postgres."));
        kit.Model.CallTools(("ask", """{ "question": "Which database?" }""")).Reply("Done.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((AgentOutcome.Completed, 2), (result.Outcome, result.Statistics.Iterations));
        Assert.Equal((HumanRequestKind.Question, "Which database?"), (kit.Human.Requests[0].Kind, kit.Human.Requests[0].Summary));
        Assert.Contains("Use Postgres.", Assert.IsType<ToolResultContent>(kit.Model.Requests[1].History[^1].Content[0]).Text, StringComparison.Ordinal);

        // CAP-02: the tool needs the capability.
        var off = Configure() with { Capabilities = new() };
        Assert.Contains(off.Validate(), error => error.Path == "tools.ask.source" && error.Problem == "needs the humanInteraction capability, which is off.");
    }

    // HITL-04, RUN-05.
    [Theory]
    [InlineData(true, true, AgentOutcome.Completed)]
    [InlineData(true, false, AgentOutcome.HandedOff)]
    [InlineData(false, false, AgentOutcome.HandedOff)]
    public async Task Exceeding_the_run_budget_waits_for_the_owners_sign_off(bool signOff, bool approve, AgentOutcome outcome)
    {
        var options = Configure() with
        {
            Run = new() { PermissionMode = PermissionMode.Auto, Budget = new() { Cost = 1 } },
            Capabilities = new() { HumanInteraction = new() { Enabled = true, SignOffs = signOff ? [SignOff.RunBudgetExceeded] : [] } },
            Providers = new Dictionary<string, ProviderOptions>
            {
                [ProviderOptions.ClaudeName] = ProviderOptions.Claude with { Prices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new() { Input = 1 } } },
            },
        };
        var kit = new TestKit(options, new Dictionary<string, ITool> { ["edit"] = edit, ["read"] = new FakeTool(ToolKind.Read) });
        kit.Human.Answer(approve ? HumanAnswer.Approve : HumanAnswer.Deny);
        kit.Model.Reply(new TextDelta("Part one."), new UsageReported(new Usage(2_000_000, 0, 0, 0)), new Stopped(StopReason.Paused)).Reply("Done.");

        var result = await kit.RunAsync(Agent, "work", Ct);

        Assert.Equal((outcome, signOff ? 1 : 0), (result.Outcome, kit.Human.Requests.Count));
        Assert.All(kit.Human.Requests, request => Assert.Equal(
            (HumanRequestKind.SignOff, "The run's cost budget is used up. Go on for another 1 USD and 08:00:00?"), (request.Kind, request.Summary)));
        Assert.Equal(outcome == AgentOutcome.Completed ? null : HandoffReason.BudgetExhausted, result.Handoff?.Reason);
    }

    // HITL-04.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_irreversible_action_waits_for_sign_off_whatever_the_tools_approval(bool on)
    {
        var setup = new ToolSetup();
        setup.Tools["edit"] = edit;
        setup.Human.Answer(HumanAnswer.Approve);
        var pipeline = setup.Create(Options(("edit", Extension("edit") with { GateExemption = "Tests only.", Irreversible = true, Approval = Approval.Never })) with
        {
            Capabilities = new() { HumanInteraction = new() { Enabled = on } },
        });

        Assert.Equal("ok", (await RunAsync(pipeline, "edit")).Content);
        Assert.Equal(on ? 1 : 0, setup.Human.Requests.Count(request => request.Summary == "an irreversible action needs the owner's sign-off"));
    }

    // RUN-06.
    [Fact]
    public async Task A_paused_agent_waits_before_its_next_model_call_while_others_go_on()
    {
        var kit = Kit();
        kit.Model.Reply("Other done.").Reply("Dev done.");

        kit.Runner.Pause(Agent);
        var dev = kit.RunAsync(Agent, "work", Ct);
        var other = await kit.RunAsync("other", "work", Ct);

        Assert.Equal(("Other done.", false, 1), (other.Output, dev.IsCompleted, kit.Model.Requests.Count));
        kit.Runner.Resume(Agent);
        Assert.Equal("Dev done.", (await dev).Output);
    }

    // RUN-06, TEST-27.
    [Fact]
    public async Task A_cancelled_agent_ends_in_a_handoff_and_its_waiting_call_is_cancelled()
    {
        var human = new WaitingHuman();
        var kit = Kit(human);
        kit.Model.CallTools(("edit", "{}"));

        var dev = kit.RunAsync(Agent, "work", Ct);
        await human.NextAsync(Ct);
        kit.Runner.Cancel(Agent);
        var result = await dev;

        Assert.Equal((HandoffReason.RequestedByHuman, "the turn was cancelled"), (result.Handoff!.Reason, result.Handoff.Detail));
        Assert.Equal(ToolErrorCategory.Cancelled, Assert.Single(result.Handoff.ToolCalls).Result.Error);
    }

    // RUN-06, TEST-27: a model call that ignores cancellation is left behind once the configured time has passed.
    [Fact]
    public async Task A_cancelled_agent_that_does_not_stop_is_left_behind_within_the_configured_time()
    {
        var time = new FakeTimeProvider();
        var model = new StuckModel();
        var options = Configure() with { Run = new() { CancelWithin = TimeSpan.FromSeconds(10) } };
        var runner = new AgentRunner(
            options, new Dictionary<string, IModelProvider> { [ProviderOptions.ClaudeName] = model }, new InMemoryStorage(),
            new Dictionary<string, ITool> { ["edit"] = edit, ["read"] = new FakeTool(ToolKind.Read) }, new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(),
            new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), time);

        var dev = runner.RunAsync(Agent, "work", ct: Ct);
        await model.Called.Task.WaitAsync(Ct);
        runner.Cancel(Agent);
        var waited = TimeSpan.Zero;
        while (!dev.IsCompleted)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            waited += TimeSpan.FromSeconds(1);
            await Task.Yield();
        }

        var result = await dev;
        Assert.Equal((HandoffReason.RequestedByHuman, "the turn was cancelled, and did not stop within 00:00:10"), (result.Handoff!.Reason, result.Handoff.Detail));
        Assert.True(waited >= TimeSpan.FromSeconds(10)); // the clock starts when the runner sees the cancellation, a moment after Cancel
    }

    /// <summary>The agents <c>dev</c> and <c>other</c>, with human interaction on, in the <c>auto</c> mode; <c>edit</c> needs approval.</summary>
    private static OfficinaOptions Configure()
    {
        var options = Options(
            ("edit", Extension("edit") with { GateExemption = "Tests only.", Approval = Approval.Always }),
            ("read", Extension("read")),
            ("ask", new() { Source = "builtin:human.ask_owner" }));
        return options with
        {
            Agents = new Dictionary<string, AgentDefinition>(options.Agents) { ["other"] = new() { Instructions = "Other work." } },
            Capabilities = new() { HumanInteraction = new() { Enabled = true } },
        };
    }

    private TestKit Kit(IHumanChannel? human = null) =>
        new(Configure(), new Dictionary<string, ITool> { ["edit"] = edit, ["read"] = new FakeTool(ToolKind.Read) }, human: human);

    /// <summary>A human who answers only when the test says so, so the test can see what goes on meanwhile.</summary>
    private sealed class WaitingHuman : IHumanChannel
    {
        private readonly Channel<(HumanRequest Request, TaskCompletionSource<HumanAnswer> Answer)> asked = Channel.CreateUnbounded<(HumanRequest, TaskCompletionSource<HumanAnswer>)>();

        /// <summary>The next request, and how to answer it.</summary>
        public async Task<(HumanRequest Request, Action<HumanAnswer> Answer)> NextAsync(CancellationToken ct)
        {
            var (request, answer) = await asked.Reader.ReadAsync(ct);
            return (request, answer.SetResult);
        }

        public ValueTask<HumanAnswer> AskAsync(HumanRequest request, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<HumanAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
            asked.Writer.TryWrite((request, answer));
            return new(answer.Task.WaitAsync(ct));
        }
    }

    /// <summary>A model whose call never ends and ignores cancellation, as a misbehaving provider might.</summary>
    private sealed class StuckModel : IModelProvider
    {
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProviderCapabilities CapabilitiesOf(string model) => ProviderCapabilities.None;

        public async IAsyncEnumerable<ModelEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Called.TrySetResult();
            await new TaskCompletionSource().Task;
            yield break;
        }
    }
}
