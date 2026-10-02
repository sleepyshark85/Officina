using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tests.Tools;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Core.Tests.Tools.ToolSetup;

namespace Sleepyshark.Officina.Core.Tests.Memory;

/// <summary>
/// Project memory (MEM). The agents <c>dev</c> and <c>lead</c> are offered the same tools: <c>read</c>, and the memory
/// tools <c>propose</c> and <c>review</c>. The owner changes memory through the runner, as the host does.
/// </summary>
public class ProjectMemoryTests
{
    private static readonly ProviderCapabilities Cached = new() { CacheBoundaries = 4, TurnScopedMessages = true };

    private static readonly Caller Other = Owner with { Id = "other", Permissions = new HashSet<string>() };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ING-02, MEM-02: a masking token stands for a value in one run only, and memory outlives the run and is read by every model call
    // after it, so a proposal that holds one is refused, never given the real value.
    [Fact]
    public async Task A_proposal_holding_a_masked_value_is_refused()
    {
        var kit = Kit();
        kit.Model.CallTools(("propose", """{ "kind": "note", "subject": "contact", "text": "Mail [email-1] about releases." }""")).Reply("Done.");

        await kit.Runner.RunAsync(Agent, "Mail ann@example.com about releases.", Owner, Ct);

        var result = Assert.IsType<ToolResultContent>(kit.Model.Requests[1].History[^1].Content[0]);
        Assert.True(result.IsError);
        Assert.Contains("holds a masked value", result.Text, StringComparison.Ordinal);
        Assert.Empty((await kit.Runner.Memory(Owner).ReadAsync(Ct)).Pending);
    }

    // MEM-01, CTX-11, TEST-09: boundary ② follows memory, and there is none while memory is empty.
    [Theory]
    [InlineData(4, true, new[] { CachePoint.Instructions, CachePoint.Memory, CachePoint.History })]
    [InlineData(2, true, new[] { CachePoint.Memory, CachePoint.History })]
    [InlineData(4, false, new[] { CachePoint.Instructions, CachePoint.History })]
    public async Task Memory_follows_the_instructions_in_the_prefix_and_boundary_two_follows_memory(int limit, bool filled, CachePoint[] expected)
    {
        var kit = Kit(capabilities: Cached with { CacheBoundaries = limit });
        if (filled)
        {
            await AddAsync(kit, Note("build", "Run dotnet test."));
        }

        kit.Model.Reply("Done.");
        await kit.Runner.RunAsync(Agent, "work", Owner, Ct);

        var request = kit.Model.Requests[0];
        Assert.Equal(filled ? "<project-memory>\n- note #1 build: Run dotnet test.\n</project-memory>" : "", request.Memory);
        Assert.Equal(expected, request.CacheBoundaries.Select(boundary => boundary.After));
        Assert.All(request.CacheBoundaries.Where(boundary => boundary.After != CachePoint.History), boundary => Assert.Equal(TimeSpan.FromHours(1), boundary.Lifetime));
    }

    // MEM-04, CTX-03, COST-01: agents, work items and callers share a prefix within one scope of memory.
    [Theory]
    [InlineData(MemoryScope.Project, true)]
    [InlineData(MemoryScope.Tenant, true)]
    [InlineData(MemoryScope.Owner, false)]
    public async Task Agents_and_callers_in_one_scope_share_a_prefix_and_other_scopes_have_other_memory(MemoryScope scope, bool shared)
    {
        var kit = Kit(new() { Enabled = true, Scope = scope });
        await AddAsync(kit, Note("build", "Run dotnet test."));
        kit.Model.Reply("one").Reply("two").Reply("three");

        await kit.Runner.RunAsync(Agent, "1", Owner, Ct);
        await kit.Runner.RunAsync(Lead, "2", Owner, Ct);
        await kit.Runner.RunAsync(Agent, "3", Other, Ct);

        var memories = kit.Model.Requests.Select(request => request.Memory).ToList();
        Assert.Equal(memories[0], memories[1]);
        Assert.Equal(shared ? memories[0] : "", memories[2]);
    }

    // MEM-04: memory outlives runs and runners, and belongs to its project.
    [Fact]
    public async Task Memory_lasts_across_runs_and_belongs_to_its_project()
    {
        var storage = new InMemoryStorage();
        var options = Configure(new() { Enabled = true });
        await AddAsync(Runner(options, storage, new()), Note("build", "Run dotnet test."));
        ScriptedModelProvider same = new() { Capabilities = Cached }, other = new() { Capabilities = Cached };
        same.Reply("one");
        other.Reply("two");

        await Runner(options, storage, same).RunAsync(Agent, "work", Owner, Ct);
        await Runner(options with { Project = new() { Name = "other-app" } }, storage, other).RunAsync(Agent, "work", Owner, Ct);

        Assert.Equal("<project-memory>\n- note #1 build: Run dotnet test.\n</project-memory>", Assert.Single(same.Requests).Memory);
        Assert.Equal("", Assert.Single(other.Requests).Memory);
    }

    // TEST-09, MEM-03: an approved change joins a running conversation as an appended operator message.
    [Fact]
    public async Task A_memory_change_does_not_edit_the_prefix_of_a_running_conversation()
    {
        var kit = Kit(onRead: kit => AddAsync(kit, Note("test", "Tests live in tests/.")));
        await AddAsync(kit, Note("build", "Run dotnet test."));
        kit.Model.CallTools(("read", """{ "path": "a.cs" }""")).Reply("Done.").Reply("Again.");

        await kit.Runner.RunAsync(Agent, "work", Owner, Ct);
        await kit.Runner.RunAsync(Agent, "again", Owner, Ct);

        var (first, second) = (kit.Model.Requests[0], kit.Model.Requests[1]);
        Assert.True(second.StartsWith(first));
        Assert.Equal(first.Memory, second.Memory);
        Assert.DoesNotContain("tests/", second.Memory, StringComparison.Ordinal);
        Assert.Equal(
            Message.System("<message from=\"operator\">\nProject memory changed:\n- note #2 test: Tests live in tests/.\n</message>"),
            second.History.Single(message => message.Role == Role.System && !message.TurnScoped));
        Assert.Contains("- note #2 test: Tests live in tests/.", kit.Model.Requests[2].Memory, StringComparison.Ordinal);
    }

    // MEM-03: a conversation that continues keeps its prefix, hears of a change once, and a new one starts with it.
    [Fact]
    public async Task A_continued_conversation_keeps_its_prefix_hears_of_a_change_once_and_a_new_one_starts_with_it()
    {
        var kit = Kit(history: HistoryStrategy.Full);
        await AddAsync(kit, Note("build", "Run dotnet test."));
        kit.Model.Reply("one").Reply("two").Reply("three").Reply("four");
        await kit.Runner.RunAsync(Agent, "1", Owner, Ct);

        await AddAsync(kit, Decision("db", "Use SQLite.", "It needs no server."));
        await kit.Runner.RunAsync(Agent, "2", Owner, Ct);
        await kit.Runner.RunAsync(Agent, "3", Owner, Ct);
        await kit.Runner.RunAsync(Agent, "4", Other, Ct);

        var requests = kit.Model.Requests;
        Assert.True(requests[1].StartsWith(requests[0]));
        Assert.Equal(requests[0].Memory, requests[1].Memory);
        var announcements = requests[2].History.Where(message => message.Role == Role.System).ToList();
        Assert.Equal(
            "<message from=\"operator\">\nProject memory changed:\n- decision #2 db: Use SQLite. Why: It needs no server. (decided by owner on 2026-10-02)\n</message>",
            Assert.IsType<TextContent>(Assert.Single(announcements).Content[0]).Text);
        Assert.True(requests[2].StartsWith(requests[1]));
        Assert.Equal(requests[0].Memory, requests[2].Memory);
        Assert.Contains("- decision #2 db: Use SQLite.", requests[3].Memory, StringComparison.Ordinal);
        Assert.DoesNotContain(requests[3].History, message => message.Role == Role.System);
    }

    // MEM-02.
    [Fact]
    public async Task A_decision_is_kept_with_its_reason_author_and_date_and_one_that_replaces_another_says_so()
    {
        var setup = Setup(out var pipeline);
        var memory = pipeline.Memory(Owner);
        setup.Time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        await CallAsync(pipeline, Agent, "propose", """{ "kind": "decision", "subject": "db", "text": "Use SQLite.", "reason": "It needs no server." }""");
        await CallAsync(pipeline, Lead, "review", """{ "id": 1, "approved": true, "reason": "Agreed." }""", lead: true);
        setup.Time.SetUtcNow(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));

        var withoutReason = await CallAsync(pipeline, Agent, "propose", """{ "kind": "decision", "subject": "db", "text": "Use Postgres." }""");
        var replaced = await CallAsync(pipeline, Agent, "propose", """{ "kind": "decision", "subject": "db", "text": "Use Postgres.", "reason": "We need many writers.", "replaces": [1] }""");
        var unknown = await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "db", "text": "x", "replaces": [9] }""");
        await CallAsync(pipeline, Lead, "review", """{ "id": 2, "approved": true, "reason": "Agreed." }""", lead: true);

        Assert.Equal("invalid arguments: a decision needs its reason.", withoutReason);
        Assert.Equal("Proposed as #2. The lead decides on it.", replaced);
        Assert.Equal("invalid arguments: #9 is not in memory, so it cannot be replaced.", unknown);
        var state = await memory.ReadAsync(Ct);
        Assert.Equal(
            "<project-memory>\n- decision #2 db: Use Postgres. Why: We need many writers. (decided by dev on 2026-11-05) Replaces #1.\n</project-memory>",
            state.Text());
        Assert.Equal(
            [(1L, "dev", new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)), (2L, "dev", new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero))],
            state.Entries.Select(entry => (entry.Id, entry.Author, entry.Date)));
    }

    // MEM-03: only the team's lead decides, by the authority the team gives it and never by its name, and never on a proposal of its own.
    [Fact]
    public async Task A_proposal_becomes_memory_when_the_lead_approves_it_and_not_before()
    {
        var setup = Setup(out var pipeline);
        var memory = pipeline.Memory(Owner);

        var proposed = await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "build", "text": "Run dotnet test." }""");
        var notLead = await CallAsync(pipeline, Agent, "review", """{ "id": 1, "approved": true, "reason": "Fine." }""");
        var namedLead = await CallAsync(pipeline, Lead, "review", """{ "id": 1, "approved": true, "reason": "Fine." }""");
        Assert.Empty((await memory.ReadAsync(Ct)).Entries);
        var approved = await CallAsync(pipeline, Lead, "review", """{ "id": 1, "approved": true, "reason": "Fine." }""", lead: true);
        var again = await CallAsync(pipeline, Lead, "review", """{ "id": 1, "approved": false, "reason": "Too late." }""", lead: true);
        await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "style", "text": "Tabs." }""");
        var rejected = await CallAsync(pipeline, Lead, "review", """{ "id": 2, "approved": false, "reason": "We use spaces." }""", lead: true);

        Assert.Equal("Proposed as #1. The lead decides on it.", proposed);
        Assert.Equal("invalid arguments: only the team's lead decides on changes.", notLead);
        Assert.Equal("invalid arguments: only the team's lead decides on changes.", namedLead);
        Assert.Equal("Approved #1.", approved);
        Assert.Equal("invalid arguments: #1 is not a proposal waiting for a decision.", again);
        Assert.Equal("Rejected #2.", rejected);
        var state = await memory.ReadAsync(Ct);
        Assert.Equal([1L], state.Entries.Select(entry => entry.Id));
        Assert.Empty(state.Pending);
        Assert.Equal(["Proposed", "Approved", "Proposed", "Rejected"], state.Log.Select(change => change.Action.ToString()));
        Assert.Empty(setup.Human.Requests);
    }

    // MEM-03, INV-02: authority is the host's, never a name, so an agent called "owner" is an agent like the others.
    [Fact]
    public async Task An_agent_named_owner_has_no_authority_over_memory()
    {
        Setup(out var pipeline);
        var memory = pipeline.Memory(Owner);
        await memory.ProposeAsync(Note("build", new string('a', 20)), Ct);
        await memory.ProposeAsync(Note("test", new string('b', 20)), Ct);
        await memory.ApproveAsync(1, null, Ct);
        await memory.ApproveAsync(2, null, Ct);

        await CallAsync(pipeline, "owner", "propose", """{ "kind": "note", "subject": "style", "text": "Tabs." }""");
        var own = await CallAsync(pipeline, "owner", "review", """{ "id": 3, "approved": true, "reason": "Fine." }""", lead: true);
        await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "all", "text": "Both.", "replaces": [1, 2] }""");
        var condensing = await CallAsync(pipeline, "owner", "review", """{ "id": 4, "approved": true, "reason": "Shorter." }""", lead: true);

        Assert.Equal("invalid arguments: you cannot decide on your own proposal.", own);
        Assert.Equal("invalid arguments: the owner reviews condensing.", condensing);
    }

    // MEM-03: where the owner approves, the owner is asked with the proposal, and a denial changes nothing.
    [Fact]
    public async Task Where_the_owner_approves_the_owner_is_asked_and_a_denial_leaves_memory_as_it_was()
    {
        var setup = Setup(out var pipeline, new() { Enabled = true, ApproveBy = MemoryApprover.Owner });
        var memory = pipeline.Memory(Owner);
        setup.Human.Answer(HumanAnswer.Deny).Answer(HumanAnswer.Approve);

        var denied = await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "build", "text": "Run dotnet test." }""");
        Assert.Empty((await memory.ReadAsync(Ct)).Log);
        var approved = await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "build", "text": "Run dotnet test." }""");
        var review = await CallAsync(pipeline, Lead, "review", """{ "id": 1, "approved": true, "reason": "Fine." }""", lead: true);

        Assert.StartsWith("approval denied", denied, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Proposed as #1. The owner approved it, so it is memory now.", approved);
        Assert.Equal("invalid arguments: #1 is not a proposal waiting for a decision.", review);
        var state = await memory.ReadAsync(Ct);
        Assert.Equal(["dev"], state.Entries.Select(entry => entry.Author));
        Assert.Equal("owner", state.Log.Single(change => change.Action == MemoryAction.Approved).By);
        Assert.Equal([HumanRequestKind.Approval, HumanRequestKind.Approval], setup.Human.Requests.Select(request => request.Kind));
        Assert.Equal("propose", setup.Human.Requests[0].Tool);
    }

    // MEM-05: nothing is dropped to make room; condensing is a proposal that replaces several entries, and the owner reviews it.
    [Fact]
    public async Task Past_its_limit_a_change_waits_and_condensing_is_reviewed_by_the_owner_and_drops_nothing()
    {
        var setup = Setup(out var pipeline, new() { Enabled = true, MaxTokens = 60 });
        var memory = pipeline.Memory(Owner);
        await memory.ProposeAsync(Note("build", new string('a', 60)), Ct);
        await memory.ApproveAsync(1, null, Ct);
        await memory.ProposeAsync(Note("test", new string('b', 60)), Ct);
        await memory.ApproveAsync(2, null, Ct);

        await CallAsync(pipeline, Agent, "propose", $$"""{ "kind": "note", "subject": "style", "text": "{{new string('c', 60)}}" }""");
        var full = await CallAsync(pipeline, Lead, "review", """{ "id": 3, "approved": true, "reason": "Fine." }""", lead: true);
        await CallAsync(pipeline, Agent, "propose", """{ "kind": "note", "subject": "all", "text": "Build and test as before.", "replaces": [1, 2] }""");
        var leadCondenses = await CallAsync(pipeline, Lead, "review", """{ "id": 4, "approved": true, "reason": "Shorter." }""", lead: true);

        Assert.StartsWith("invalid arguments: memory would be ", full, StringComparison.Ordinal);
        Assert.EndsWith("tokens, over its limit of 60. Propose a condensed version that replaces several entries with one; the owner reviews it.", full, StringComparison.Ordinal);
        Assert.Equal("invalid arguments: the owner reviews condensing.", leadCondenses);
        Assert.Equal([3L, 4L], (await memory.ReadAsync(Ct)).Pending.Select(proposal => proposal.Id));

        Assert.True((await memory.ApproveAsync(4, "Condensed.", Ct)).Accepted);
        Assert.Equal("Approved #3.", await CallAsync(pipeline, Lead, "review", """{ "id": 3, "approved": true, "reason": "Fine now." }""", lead: true));

        var state = await memory.ReadAsync(Ct);
        Assert.Equal([4L, 3L], state.Current().Select(entry => entry.Id));
        Assert.Equal([1L, 2L, 4L, 3L], state.Entries.Select(entry => entry.Id));
        Assert.Contains("Replaces #1, #2.", state.Text(), StringComparison.Ordinal);
        Assert.Contains(state.Log, change => change.Content?.Text == new string('a', 60));
    }

    // CAP-02, CAP-03.
    [Fact]
    public void The_memory_tools_need_the_capability_and_owner_approval_needs_human_interaction()
    {
        var options = Options(("propose", new() { Source = "builtin:memory.propose_change", GateExemption = Exempt }), ("review", new() { Source = "builtin:memory.review", GateExemption = Exempt })) with
        {
            Capabilities = new() { ProjectMemory = new() { Enabled = true, ApproveBy = MemoryApprover.Owner } },
        };
        var off = options with { Capabilities = new() };

        Assert.Equal(["capabilities.projectMemory.approveBy"], options.Validate().Select(error => error.Path));
        Assert.Equal(["tools.propose.source", "tools.review.source"], off.Validate().Select(error => error.Path));
        Assert.Throws<InvalidOperationException>(() => new TestKit(new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { [Agent] = new() { Instructions = "Work." } } }).Runner.Memory(Owner));
    }

    private static MemoryProposal Note(string subject, string text) => new(MemoryKind.Note, subject, text);

    private static MemoryProposal Decision(string subject, string text, string reason) => new(MemoryKind.Decision, subject, text, reason);

    /// <summary>The owner adds an entry to the kit's memory.</summary>
    private static Task AddAsync(TestKit kit, MemoryProposal proposal) => AddAsync(kit.Runner, proposal);

    private static async Task AddAsync(AgentRunner runner, MemoryProposal proposal)
    {
        var memory = runner.Memory(Owner);
        var (id, _) = await memory.ProposeAsync(proposal, Ct);
        Assert.True((await memory.ApproveAsync(id!.Value, null, Ct)).Accepted);
    }

    private static AgentRunner Runner(OfficinaOptions options, InMemoryStorage storage, ScriptedModelProvider model) => new(
        options, options.Providers.Keys.ToDictionary(name => name, IModelProvider (_) => model), storage,
        new Dictionary<string, ITool> { ["read"] = new FakeTool(ToolKind.Read) }, new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(),
        new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider());

    private static TestKit Kit(
        ProjectMemoryOptions? memory = null, HistoryStrategy history = HistoryStrategy.None, ProviderCapabilities? capabilities = null, Func<TestKit, Task>? onRead = null)
    {
        TestKit kit = null!;
        var read = new FakeTool(ToolKind.Read, run: async (_, _) =>
        {
            if (onRead is not null)
            {
                await onRead(kit);
            }

            return ToolResult.Success("a.cs");
        });
        kit = new TestKit(Configure(memory ?? new() { Enabled = true }, history), new Dictionary<string, ITool> { ["read"] = read }, capabilities: capabilities ?? Cached);
        kit.Time.SetUtcNow(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        return kit;
    }

    private const string Lead = "lead";

    private const string Exempt = "Its proposals wait for approval.";

    private static OfficinaOptions Configure(ProjectMemoryOptions memory, HistoryStrategy history = HistoryStrategy.None)
    {
        var options = Options(
            ("read", Extension("read")), ("propose", new() { Source = "builtin:memory.propose_change", GateExemption = Exempt }), ("review", new() { Source = "builtin:memory.review", GateExemption = Exempt }));
        var dev = options.Agents[Agent] with { Context = new() { History = new() { Strategy = history } } };
        return options with
        {
            Project = new() { Name = "app" },
            Agents = new Dictionary<string, AgentDefinition> { [Agent] = dev, [Lead] = dev, ["owner"] = dev },
            Capabilities = new()
            {
                ProjectMemory = memory, ConversationStore = new() { Enabled = history != HistoryStrategy.None }, HumanInteraction = new() { Enabled = true },
            },
        };
    }

    /// <summary>A real tool pipeline for the memory tools; the human is the only stand-in besides the clock.</summary>
    private static ToolSetup Setup(out ToolPipeline pipeline, ProjectMemoryOptions? memory = null)
    {
        var setup = new ToolSetup();
        setup.Tools["read"] = new FakeTool(ToolKind.Read);
        pipeline = setup.Create(Configure(memory ?? new() { Enabled = true }));
        return setup;
    }

    /// <summary>Calls a memory tool as an agent; the team gives its lead the lead's authority, never the agent's name.</summary>
    private static async Task<string> CallAsync(ToolPipeline pipeline, string agent, string tool, string arguments, bool lead = false) =>
        (await RunAsync(pipeline, tool, arguments, Context with { Agent = agent, Lead = lead })).Content;
}
