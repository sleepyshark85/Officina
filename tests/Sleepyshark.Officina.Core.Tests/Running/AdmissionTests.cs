using System.Text.Json;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Running;

/// <summary>Admission: masking (ING-02, ING-06, TEST-19), rate limits and rejections (ING-01, ING-03, ING-04), and the untrusted mark (SEC-04).</summary>
public class AdmissionTests
{
    private const string Email = "ann@example.com";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // TEST-19, ING-02, ING-06.
    [Fact]
    public async Task Masking_is_on_by_default_and_masked_values_reach_only_the_tools_allowed_to_see_them()
    {
        var send = new FakeTool(ToolKind.Write, run: (call, _) => ValueTask.FromResult(ToolResult.Success($"sent to {call.Arguments.GetProperty("to")}")));
        var note = new FakeTool(ToolKind.Write);
        var kit = new TestKit(
            Options(("send", Extension("send") with { ReceivesMaskedValues = true, GateExemption = "Tests only." }), ("note", Extension("note") with { GateExemption = "Tests only." })),
            new Dictionary<string, ITool> { ["send"] = send, ["note"] = note });
        kit.Model.CallTools(("send", """{ "to": "[email-1]" }"""), ("note", """{ "text": "[email-1]" }""")).Reply("Done.");

        var result = await kit.RunAsync(Agent, $"Write to {Email} or call +44 20 7946 0958.", Ct);

        Assert.Equal(Message.User("Write to [email-1] or call [phone-2]."), kit.Model.Requests[0].History[0]);
        Assert.Equal(Email, send.Calls[0].Arguments.GetProperty("to").GetString());
        Assert.Equal("[email-1]", note.Calls[0].Arguments.GetProperty("text").GetString());
        Assert.Contains("sent to [email-1]", Text(kit.Model.Requests[1].History), StringComparison.Ordinal);
        Assert.Equal("Done.", result.Output);

        // The audit log holds the error details the log writes, so neither sees the value.
        var events = await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs[0].RunId, 0, Ct);
        Assert.All([Text(kit.Model.Requests.SelectMany(request => request.History)), Text(result.Transcript), Json(kit.Storage.Audit.Entries), Json(events)],
            seen => Assert.DoesNotContain(Email, seen, StringComparison.Ordinal));
    }

    // ING-02.
    [Fact]
    public async Task Results_passages_and_messages_are_masked_with_the_same_token_and_masking_can_be_turned_off()
    {
        var tools = new Dictionary<string, ITool> { ["lookup"] = new FakeTool(ToolKind.Read, run: (_, _) => ValueTask.FromResult(ToolResult.Success(Email))) };
        var knowledge = new Dictionary<string, IKnowledgeSource> { ["crm"] = new FakeKnowledgeSource(Coverage.Covered, ("crm:1", $"Ann is {Email}.")) };
        var options = Options(("lookup", Extension("lookup") with { MaskResults = true }));
        options = options with
        {
            Knowledge = new Dictionary<string, KnowledgeOptions> { ["crm"] = new() { Use = "extension:crm", Mask = true } },
            Capabilities = new() { Knowledge = new() { Enabled = true } },
            Agents = new Dictionary<string, AgentDefinition>
            {
                [Agent] = options.Agents[Agent] with { Context = new() { Retrieval = new() { BeforeTurn = ["crm"] } } },
            },
        };
        var kit = new TestKit(options, tools, knowledge: knowledge);
        kit.Runner.Send(Agent, Sender.Owner, $"Also {Email}.");
        kit.Model.CallTools(("lookup", "{}")).Reply("Done.");

        await kit.RunAsync(Agent, $"Find {Email}.", Ct);

        var seen = Text(kit.Model.Requests[^1].History);
        Assert.DoesNotContain(Email, seen, StringComparison.Ordinal);
        Assert.Equal(4, seen.Split("[email-1]").Length - 1); // the work, the message, the passage and the result

        var unmasked = new TestKit(options with { Policies = new() { Masking = new() { Enabled = false } } }, tools, knowledge: knowledge);
        unmasked.Model.Reply("Done.");
        await unmasked.RunAsync(Agent, $"Find {Email}.", Ct);
        Assert.Equal(Message.User($"Find {Email}."), unmasked.Model.Requests[0].History[0]);
    }

    // ING-02, SBX-04: a tool's streamed output is masked under the same condition as its result.
    [Fact]
    public async Task Tool_output_events_are_masked_like_the_tools_results()
    {
        async ValueTask<ToolResult> Stream(ToolCall call, CancellationToken ct)
        {
            await call.Output($"to {Email}", ct);
            return ToolResult.Success("ok");
        }

        var tools = new Dictionary<string, ITool> { ["masked"] = new FakeTool(ToolKind.Read, run: Stream), ["plain"] = new FakeTool(ToolKind.Read, run: Stream) };
        var kit = new TestKit(Options(("masked", Extension("masked") with { MaskResults = true }), ("plain", Extension("plain"))), tools);
        kit.Model.CallTools(("masked", "{}"), ("plain", "{}")).Reply("Done.");

        await kit.RunAsync(Agent, "Go.", Ct);

        var events = await kit.Storage.Events.ReadAsync(null, kit.Storage.Runs.Runs[0].RunId, 0, Ct);
        Assert.Equal([("masked", "to [email-1]"), ("plain", $"to {Email}")], events.Select(read => read.Payload).OfType<ToolOutput>().Select(output => (output.Tool, output.Line)));
    }

    // ING-03, ING-04.
    [Fact]
    public async Task Rate_limits_per_owner_and_per_tenant_reject_work_with_a_reason()
    {
        var limits = new RateLimitOptions { PerOwner = new() { Permits = 1, Window = TimeSpan.FromHours(1) }, PerTenant = new() { Permits = 2, Window = TimeSpan.FromHours(1) } };
        var kit = new TestKit(Options() with { Policies = new() { RateLimits = limits } });
        kit.Model.Reply("1").Reply("2").Reply("3");

        AgentResult[] results =
        [
            await kit.Runner.RunAsync(Agent, "work", Caller("ann", "acme"), Ct),
            await kit.Runner.RunAsync(Agent, "work", Caller("ann", "acme"), Ct),
            await kit.Runner.RunAsync(Agent, "work", Caller("bob", "acme"), Ct),
            await kit.Runner.RunAsync(Agent, "work", Caller("carl", "acme"), Ct),
            await kit.Runner.RunAsync(Agent, "work", Caller("dan", "other"), Ct),
        ];

        Assert.Equal(
            [
                (AgentOutcome.Completed, "1"), (AgentOutcome.Rejected, "the owner's rate limit is reached"), (AgentOutcome.Completed, "2"),
                (AgentOutcome.Rejected, "the tenant's rate limit is reached"), (AgentOutcome.Completed, "3"),
            ],
            results.Select(result => (result.Outcome, result.Output)));
        Assert.Equal(3, kit.Model.Requests.Count);

        // A new window starts once the last one has ended.
        kit.Time.Advance(TimeSpan.FromHours(1));
        kit.Model.Reply("4");
        Assert.Equal(AgentOutcome.Completed, (await kit.Runner.RunAsync(Agent, "work", Caller("ann", "acme"), Ct)).Outcome);
    }

    // ING-01: the trigger check comes first.
    [Fact]
    public async Task The_first_rejection_wins()
    {
        var options = Options() with { Policies = new() { RateLimits = new() { PerOwner = new() { Permits = 1, Window = TimeSpan.FromHours(1) } } } };
        var kit = new TestKit(options with { Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Triggers = [Trigger.Batch] } } });
        kit.Model.Reply("done");

        var batch = await kit.Runner.RunBatchAsync(Agent, ["first"], ct: Ct);
        var request = await kit.Runner.RunAsync(Agent, "second", ct: Ct);

        Assert.Equal(AgentOutcome.Completed, Assert.Single(batch).Outcome);
        Assert.Equal((AgentOutcome.Rejected, $"agent {Agent} does not take work by request"), (request.Outcome, request.Output));
    }

    [Fact]
    public void Masking_patterns_must_be_valid_and_rate_limits_real()
    {
        var policies = new PolicyOptions
        {
            Masking = new() { Patterns = new Dictionary<string, string> { ["badName-"] = "x", ["unclosed"] = "(", ["employeeId"] = "EMP-[0-9]{6}" } },
            RateLimits = new() { PerTenant = new() { Permits = 0, Window = TimeSpan.Zero } },
        };

        // The provider runs its own tools and gives the model their results, so the core cannot mask them.
        // The core's own tools work on what the model has seen, masked already.
        var tools = new Dictionary<string, ToolOptions>
        {
            ["web_search"] = new() { Source = "provider:web_search", Reason = "Research.", MaskResults = true },
            ["cite"] = new() { Source = "builtin:record.cite", ReceivesMaskedValues = true },
        };

        Assert.Equal(
            [
                "policies.masking.patterns.badName-", "policies.masking.patterns.unclosed", "policies.rateLimits.perTenant.permits", "policies.rateLimits.perTenant.window",
                "tools.cite.receivesMaskedValues", "tools.web_search.maskResults",
            ],
            new OfficinaOptions { Policies = policies, Tools = tools }.Validate().Select(error => error.Path).Order(StringComparer.Ordinal));
    }

    // ING-06: tokens are numbered per run, so kept history could name another run's value.
    [Fact]
    public void Kept_history_is_refused_while_masked_values_reach_a_tool()
    {
        var options = Options(("send", Extension("send") with { ReceivesMaskedValues = true, GateExemption = "Tests only." }));
        options = options with
        {
            Capabilities = new() { ConversationStore = new() { Enabled = true } },
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = options.Agents[Agent] with { Context = new() { History = new() { Strategy = HistoryStrategy.Full } } } },
        };

        Assert.Equal($"agents.{Agent}.context.history.strategy", Assert.Single(options.Validate()).Path);
        Assert.Empty((options with { Policies = new() { Masking = new() { Enabled = false } } }).Validate());
    }

    // SEC-04: whether the core or the provider runs the untrusted tool.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_agent_that_has_read_untrusted_content_is_marked_and_gates_act_on_the_mark(bool byProvider)
    {
        var fetch = byProvider ? new ToolOptions { Source = "provider:fetch", Reason = "Research." } : Extension("fetch");
        var options = Options(("fetch", fetch with { Untrusted = true }), ("post", Extension("post") with { Gates = ["untrusted"] }));
        options = options with { Gates = new Dictionary<string, GateOptions> { ["untrusted"] = new() { Use = GateOptions.UntrustedContentApproval } } };
        var kit = new TestKit(
            options, new Dictionary<string, ITool> { ["fetch"] = new FakeTool(ToolKind.Read), ["post"] = new FakeTool(ToolKind.Write) },
            capabilities: new() { ProviderTools = new HashSet<string> { "fetch" } });
        kit.Model.CallTools(("post", "{}"));
        if (byProvider)
        {
            kit.Model.Reply(new ProviderToolUsed(new ToolRequest("fetch", Args("{}")), "page"), new Stopped(StopReason.Paused));
        }
        else
        {
            kit.Model.CallTools(("fetch", "{}"));
        }

        kit.Model.CallTools(("post", "{}")).Reply("Done.");
        kit.Human.Answer(HumanAnswer.Approve);

        await kit.RunAsync(Agent, "Post the page.", Ct);

        Assert.Equal("post", Assert.Single(kit.Human.Requests).Tool);
    }

    private static Caller Caller(string id, string tenant) => new(id, tenant, new HashSet<string>(), new Dictionary<string, string>());

    private static string Text(IEnumerable<Message> messages) => string.Join('\n', messages.SelectMany(message => message.Content).Select(content => content switch
    {
        TextContent text => text.Text,
        ToolUseContent use => use.Arguments.GetRawText(),
        ToolResultContent result => result.Text,
        _ => "",
    }));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
}
