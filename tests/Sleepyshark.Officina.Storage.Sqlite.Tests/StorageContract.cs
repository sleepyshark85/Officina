using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core;
using Sleepyshark.Officina.Core.Checkpoints;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Records;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Memory;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Core.Tools;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Storage.Sqlite.Tests;

/// <summary>What every storage does (STO-01). The in-memory and the SQLite storage both pass it (DESIGN.md §11).</summary>
public abstract class StorageContract
{
    protected static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IStorage> CreateAsync();

    [Fact]
    public async Task A_run_is_stored_with_the_configuration_it_used()
    {
        var storage = await CreateAsync();
        var configuration = new OfficinaOptions { Project = new() { Name = "invoice-api" } };
        await storage.Runs.RecordStartAsync("acme", Run("run-1", "ann") with { Configuration = configuration }, Ct);

        var run = Assert.Single((await storage.ExportAsync("acme", "ann", Ct)).Runs);

        Assert.Equal(("run-1", "extractor", "ann", Start, CoreVersion.Value), (run.RunId, run.Agent, run.Owner, run.Time, run.CoreVersion));
        Assert.Equal(Json(configuration), Json(run.Configuration));
    }

    // EVT-03.
    [Fact]
    public async Task Events_are_read_in_order_after_any_sequence_number()
    {
        var storage = await CreateAsync();
        foreach (var sequence in new[] { 1, 2, 5 })
        {
            await storage.Events.AppendAsync("acme", Event("run-1", sequence), Ct);
        }

        await storage.Events.AppendAsync("acme", Event("run-2", 3), Ct);

        Assert.Equal([1L, 2, 5], (await storage.Events.ReadAsync("acme", "run-1", 0, Ct)).Select(read => read.Sequence));
        Assert.Equal([Event("run-1", 5)], await storage.Events.ReadAsync("acme", "run-1", 2, Ct));
    }

    [Fact]
    public async Task Audit_entries_are_read_in_the_order_appended()
    {
        var storage = await CreateAsync();
        await storage.Audit.AppendAsync("acme", Entry("run-1", AuditOutcome.Intent), Ct);
        await storage.Audit.AppendAsync("acme", Entry("run-1", AuditOutcome.Failed) with { Detail = "IOException: disk full" }, Ct);

        Assert.Equal(
            [Entry("run-1", AuditOutcome.Intent), Entry("run-1", AuditOutcome.Failed) with { Detail = "IOException: disk full" }],
            await storage.Audit.ReadAsync("acme", "run-1", Ct));
    }

    // CAP-05, HIST-01: a shortened turn holds the whole conversation, so reading starts there.
    [Fact]
    public async Task A_conversation_is_read_in_order_from_its_latest_shortened_turn()
    {
        var storage = await CreateAsync();
        ConversationTurn[] turns =
        [
            Turn("ann", Message.User("1")),
            Turn("ann", Message.User("Summary of 1."), Message.User("2")) with { Shortened = true, PrefixMemory = 2, SeenMemory = 3 },
            Turn("ann", Message.User("3"), new Message(Role.Assistant, [new ReasoningContent("Look first.", "sig"), new ToolUseContent("call-1", "read", Args)]),
                new Message(Role.User, [new ToolResultContent("call-1", "text", isError: false)]), new Message(Role.System, [new TextContent("<context />")], turnScoped: true),
                new Message(Role.Assistant, [new ProviderContent(Args)])),
        ];
        foreach (var turn in turns)
        {
            await storage.Conversations.AppendAsync("acme", turn, Ct);
        }

        await storage.Conversations.AppendAsync("acme", Turn("bob", Message.User("other")), Ct);
        await storage.Conversations.AppendAsync("acme", Turn("ann", Message.User("other")) with { Agent = "reviewer" }, Ct);

        var read = await storage.Conversations.ReadAsync("acme", "extractor", "ann", Ct);

        // Arguments and provider content are JSON, which compares by its text.
        Assert.Equal(JsonSerializer.Serialize(turns[1..].SelectMany(turn => turn.Messages)), JsonSerializer.Serialize(read.SelectMany(turn => turn.Messages)));
        Assert.Equal([(true, Start), (false, Start)], read.Select(turn => (turn.Shortened, turn.Time)));
        Assert.Equal([(2L, 3L), (0L, 0L)], read.Select(turn => (turn.PrefixMemory, turn.SeenMemory)));
    }

    // REC-01, REC-04.
    [Fact]
    public async Task A_record_is_read_in_revision_order_and_a_taken_revision_is_refused()
    {
        var storage = await CreateAsync();
        Assert.True(await storage.Records.TryAppendAsync("acme", Fact("run-1", 1), Ct));
        Assert.True(await storage.Records.TryAppendAsync("acme", Fact("run-1", 2), Ct));
        Assert.True(await storage.Records.TryAppendAsync("acme", Fact("run-2", 1), Ct));

        Assert.False(await storage.Records.TryAppendAsync("acme", Fact("run-1", 2) with { Agent = "reviewer" }, Ct));

        Assert.Equal([Fact("run-1", 1), Fact("run-1", 2)], await storage.Records.ReadAsync("acme", "run-1", Ct));
    }

    // TASK-07, TASK-04.
    [Fact]
    public async Task A_task_board_is_read_in_revision_order_and_a_taken_revision_is_refused()
    {
        var storage = await CreateAsync();
        Assert.True(await storage.Tasks.TryAppendAsync("acme", Change("run-1", 1), Ct));
        Assert.True(await storage.Tasks.TryAppendAsync("acme", Change("run-1", 2), Ct));
        Assert.True(await storage.Tasks.TryAppendAsync("acme", Change("run-2", 1), Ct));

        Assert.False(await storage.Tasks.TryAppendAsync("acme", Change("run-1", 2) with { By = "reviewer" }, Ct));

        Assert.Equal(Json(Change("run-1", 1), Change("run-1", 2)), Json([.. await storage.Tasks.ReadAsync("acme", "run-1", Ct)]));
    }

    // MEM-03, MEM-04, CONC-01: a scope's log is its own, within the tenant, and a taken revision is refused.
    [Fact]
    public async Task Project_memory_is_read_in_revision_order_and_a_taken_revision_is_refused()
    {
        var storage = await CreateAsync();
        foreach (var tenant in new[] { "acme", null })
        {
            Assert.True(await storage.Memory.TryAppendAsync(tenant, Proposed("project:app", 1), Ct));
            Assert.True(await storage.Memory.TryAppendAsync(tenant, Approved("project:app", 2), Ct));
            Assert.True(await storage.Memory.TryAppendAsync(tenant, Proposed("owner:ann", 1), Ct));

            Assert.False(await storage.Memory.TryAppendAsync(tenant, Approved("project:app", 1), Ct));

            Assert.Equal(Json(Proposed("project:app", 1), Approved("project:app", 2)), Json([.. await storage.Memory.ReadAsync(tenant, "project:app", Ct)]));
        }

        Assert.Empty(await storage.Memory.ReadAsync("globex", "project:app", Ct));
    }

    // TOOL-09, OUT-05.
    [Fact]
    public async Task An_artifact_is_read_by_its_id_within_its_run()
    {
        var storage = await CreateAsync();
        var first = await storage.Artifacts.SaveAsync("acme", "run-1", new("read_log result", "full text"), Start, Ct);
        var second = await storage.Artifacts.SaveAsync("acme", "run-1", new("report.md", "# Report"), Start, Ct);

        Assert.NotEqual(first, second);
        Assert.Equal(new Artifact("report.md", "# Report"), await storage.Artifacts.ReadAsync("acme", "run-1", second, Ct));
        Assert.Null(await storage.Artifacts.ReadAsync("acme", "run-2", first, Ct));
    }

    // REC-04, CONC-01: real threads propose to one record through the real pipeline, record tool and store.
    [Fact]
    public async Task Concurrent_proposals_never_overwrite_each_other()
    {
        const int Writers = 4;
        const int Proposals = 25;
        var storage = await CreateAsync();
        var options = new OfficinaOptions
        {
            Agents = Enumerable.Range(0, Writers).ToDictionary(writer => $"agent-{writer}", _ => new AgentDefinition { Instructions = "Record.", Tools = ["record"] }),
            Tools = new Dictionary<string, ToolOptions> { ["propose_fact"] = new() { Source = "builtin:record.propose_fact" } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["record"] = ["propose_fact"] },
        };
        var time = new FakeTimeProvider(Start);
        var pipeline = new ToolPipeline(
            options, new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, IKnowledgeSource>(), new Dictionary<string, ICheck>(), storage,
            new EventBus(storage.Events, options.Storage, time), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), time);
        var caller = new Caller("ann", "acme", new HashSet<string>(), new Dictionary<string, string>());
        var errors = new ConcurrentBag<string>();
        using var start = new Barrier(Writers);
        var threads = Enumerable.Range(0, Writers).Select(writer => new Thread(() =>
        {
            start.SignalAndWait();
            for (var proposal = 0; proposal < Proposals; proposal++)
            {
                var arguments = JsonDocument.Parse($$"""{ "subject": "{{writer}}.{{proposal}}", "value": "v", "source": "test" }""").RootElement;
                var context = new ToolContext("run-1", $"agent-{writer}", caller);
                var result = pipeline.RunAsync(context, [new ToolRequest("propose_fact", arguments)], CancellationToken.None).GetAwaiter().GetResult()[0];
                if (result.Error is not null)
                {
                    errors.Add(result.Content);
                }
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        Assert.Empty(errors);
        var record = await storage.Records.ReadAsync("acme", "run-1", Ct);
        Assert.Equal(Enumerable.Range(1, Writers * Proposals).Select(revision => (long)revision), record.Select(entry => entry.Revision));
        Assert.All(record, entry => Assert.StartsWith($"{entry.Agent[^1]}.", ((Fact)entry.Item).Subject, StringComparison.Ordinal));
        Assert.Equal(Writers * Proposals, record.Select(entry => ((Fact)entry.Item).Subject).Distinct().Count());
    }

    // TASK-04, CONC-01: real threads claim one task through the real pipeline, task tool and store.
    [Fact]
    public async Task Only_one_of_many_agents_claiming_a_task_at_once_gets_it()
    {
        const int Agents = 4;
        var storage = await CreateAsync();
        var options = new OfficinaOptions
        {
            Agents = Enumerable.Range(0, Agents).ToDictionary(agent => $"agent-{agent}", _ => new AgentDefinition { Instructions = "Claim.", Tools = ["tasks"] }),
            Tools = new Dictionary<string, ToolOptions> { ["claim"] = new() { Source = "builtin:tasks.claim" } },
            ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["tasks"] = ["claim"] },
            Capabilities = new() { TaskBoard = new() { Enabled = true } },
        };
        var time = new FakeTimeProvider(Start);
        var pipeline = new ToolPipeline(
            options, new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, IKnowledgeSource>(), new Dictionary<string, ICheck>(), storage,
            new EventBus(storage.Events, options.Storage, time), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), time);
        await pipeline.Board("acme", "run-1").AddAsync("t1", new() { Title = "Fix the parser" }, "planned", Ct);
        var caller = new Caller("ann", "acme", new HashSet<string>(), new Dictionary<string, string>());
        var claimed = new ConcurrentBag<string>();
        using var start = new Barrier(Agents);
        var threads = Enumerable.Range(0, Agents).Select(agent => new Thread(() =>
        {
            start.SignalAndWait();
            var context = new ToolContext("run-1", $"agent-{agent}", caller);
            var arguments = JsonDocument.Parse("""{ "id": "t1" }""").RootElement;
            if (pipeline.RunAsync(context, [new ToolRequest("claim", arguments)], CancellationToken.None).GetAwaiter().GetResult()[0].Error is null)
            {
                claimed.Add(context.Agent);
            }
        })).ToList();

        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());

        var task = Assert.Single(await pipeline.Board("acme", "run-1").ReadAsync(Ct));
        Assert.Equal((TaskState.InProgress, Assert.Single(claimed)), (task.State, task.Assignee));
    }

    // SEC-02, TEST-18.
    [Fact]
    public async Task A_tenant_cannot_see_another_tenants_data()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");

        foreach (var other in new[] { "globex", null })
        {
            Assert.Empty(await storage.Events.ReadAsync(other, "run-1", 0, Ct));
            Assert.Empty(await storage.Audit.ReadAsync(other, "run-1", Ct));
            Assert.Empty(await storage.Conversations.ReadAsync(other, "extractor", "ann", Ct));
            Assert.Empty(await storage.Records.ReadAsync(other, "run-1", Ct));
            Assert.Null(await storage.Artifacts.ReadAsync(other, "run-1", 1, Ct));
            Assert.Empty(await storage.Tasks.ReadAsync(other, "run-1", Ct));
            Assert.Equal(0, await CountAsync(storage, other, "ann"));
            await storage.DeleteAsync(other, "ann", Ct);
        }

        Assert.Single((await storage.ExportAsync("acme", "ann", Ct)).Runs);
    }

    // RUN-01, RUN-04.
    [Fact]
    public async Task A_run_keeps_its_status_and_the_work_it_was_given_so_that_a_crashed_run_can_start_again()
    {
        var storage = await CreateAsync();
        await storage.Runs.RecordStartAsync("acme", Run("run-1", "ann") with { Input = "Fix bug 7.", Trigger = Trigger.LongRunning, TaskId = "t1" }, Ct);
        await storage.Runs.RecordStartAsync("acme", Run("run-2", "ann"), Ct);

        var started = Assert.IsType<StoredRun>(await storage.Runs.ReadAsync("acme", "run-1", Ct));
        await storage.Runs.RecordStatusAsync("acme", "run-2", RunStatus.Completed, Ct);

        Assert.Equal((RunStatus.Running, "Fix bug 7.", Trigger.LongRunning, "t1"), (started.Status, started.Started.Input, started.Started.Trigger, started.Started.TaskId));
        Assert.Equal(RunStatus.Completed, (await storage.Runs.ReadAsync("acme", "run-2", Ct))!.Status);
        Assert.Null(await storage.Runs.ReadAsync("other", "run-1", Ct));
        Assert.Null(await storage.Runs.ReadAsync("acme", "run-3", Ct));
    }

    // RUN-03.
    [Fact]
    public async Task Checkpoints_are_read_in_order_and_those_after_a_number_are_deleted()
    {
        var storage = await CreateAsync();
        foreach (var number in new[] { 1, 0, 2 })
        {
            await storage.Checkpoints.AppendAsync("acme", Saved("run-1", number), Ct);
        }

        await storage.Checkpoints.AppendAsync("acme", Saved("run-2", 0), Ct);

        Assert.Equal([0, 1, 2], (await storage.Checkpoints.ReadAsync("acme", "run-1", Ct)).Select(checkpoint => checkpoint.Number));
        Assert.Equal(Saved("run-1", 2).Workspace, (await storage.Checkpoints.ReadAsync("acme", "run-1", Ct))[2].Workspace);
        Assert.Equal(new Dictionary<string, int> { ["extractor"] = 3 }, (await storage.Checkpoints.ReadAsync("acme", "run-1", Ct))[0].Conversations);
        Assert.Empty(await storage.Checkpoints.ReadAsync("other", "run-1", Ct));

        await storage.Checkpoints.TruncateAsync("acme", "run-1", 0, Ct);

        Assert.Equal([0], (await storage.Checkpoints.ReadAsync("acme", "run-1", Ct)).Select(checkpoint => checkpoint.Number));
        Assert.Single(await storage.Checkpoints.ReadAsync("acme", "run-2", Ct));
    }

    // RUN-08: going back deletes what came after a position, and only there.
    [Fact]
    public async Task Going_back_deletes_the_conversation_record_board_and_memory_after_a_position()
    {
        var storage = await CreateAsync();
        foreach (var revision in new[] { 1, 2, 3 })
        {
            await storage.Records.TryAppendAsync("acme", Fact("run-1", revision), Ct);
            await storage.Records.TryAppendAsync("acme", Fact("run-2", revision), Ct);
            await storage.Tasks.TryAppendAsync("acme", Change("run-1", revision), Ct);
            await storage.Memory.TryAppendAsync("acme", Proposed("project:x", revision), Ct);
            await storage.Conversations.AppendAsync("acme", Turn("ann", Message.User($"turn {revision}")), Ct);
        }

        await storage.Conversations.AppendAsync("acme", Turn("bob", Message.User("bob")), Ct);

        await storage.Records.TruncateAsync("acme", "run-1", 1, Ct);
        await storage.Tasks.TruncateAsync("acme", "run-1", 2, Ct);
        await storage.Memory.TruncateAsync("acme", "project:x", 0, Ct);
        await storage.Conversations.TruncateAsync("acme", "extractor", "ann", 1, Ct);

        Assert.Equal([1L], (await storage.Records.ReadAsync("acme", "run-1", Ct)).Select(entry => entry.Revision));
        Assert.Equal(3, (await storage.Records.ReadAsync("acme", "run-2", Ct)).Count);
        Assert.Equal([1L, 2], (await storage.Tasks.ReadAsync("acme", "run-1", Ct)).Select(change => change.Revision));
        Assert.Empty(await storage.Memory.ReadAsync("acme", "project:x", Ct));
        Assert.Equal(1, await storage.Conversations.CountAsync("acme", "extractor", "ann", Ct));
        Assert.Equal(1, await storage.Conversations.CountAsync("acme", "extractor", "bob", Ct));
        Assert.Equal(0, await storage.Conversations.CountAsync("other", "extractor", "ann", Ct));
    }

    // PRIV-02, TEST-18.
    [Fact]
    public async Task An_owners_data_is_exported_on_request()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");
        await StoreRunAsync(storage, "acme", "run-2", "bob");

        var export = await storage.ExportAsync("acme", "ann", Ct);

        Assert.Equal(["run-1"], export.Runs.Select(run => run.RunId));
        Assert.Equal(["owner:ann"], export.Memory.Select(change => change.Scope));
        Assert.Equal([Event("run-1", 1)], export.Events);
        Assert.Equal([Entry("run-1", AuditOutcome.Intent)], export.Audit);
        Assert.Equal(["ann"], export.Conversations.Select(turn => turn.Owner));
        Assert.Equal([Fact("run-1", 1)], export.Records);
        Assert.Equal([new Artifact("run-1 result", "full text")], export.Artifacts);
        Assert.Equal(Json(Change("run-1", 1)), Json([.. export.Tasks]));
        Assert.Equal([("run-1", 0)], export.Checkpoints.Select(checkpoint => (checkpoint.RunId, checkpoint.Number)));
    }

    // PRIV-02, TEST-18.
    [Fact]
    public async Task Deleting_an_owners_data_leaves_audit_entries_to_their_retention()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");
        await StoreRunAsync(storage, "acme", "run-2", "bob");

        await storage.DeleteAsync("acme", "ann", Ct);

        Assert.Equal(0, await CountAsync(storage, "acme", "ann"));
        Assert.Empty(await storage.Events.ReadAsync("acme", "run-1", 0, Ct));
        Assert.Empty(await storage.Conversations.ReadAsync("acme", "extractor", "ann", Ct));
        Assert.Empty(await storage.Records.ReadAsync("acme", "run-1", Ct));
        Assert.Empty(await storage.Tasks.ReadAsync("acme", "run-1", Ct));
        Assert.Empty(await storage.Checkpoints.ReadAsync("acme", "run-1", Ct));
        Assert.Equal([Entry("run-1", AuditOutcome.Intent)], await storage.Audit.ReadAsync("acme", "run-1", Ct));
        Assert.Single((await storage.ExportAsync("acme", "bob", Ct)).Runs);

        // PRIV-02: an owner's own memory goes with the owner's data; the project's and others' stay.
        Assert.Empty(await storage.Memory.ReadAsync("acme", "owner:ann", Ct));
        Assert.Single(await storage.Memory.ReadAsync("acme", "owner:bob", Ct));
    }

    // PRIV-01, EVT-05, TEST-18; REC-05: a record is deleted whole, never trimmed.
    [Fact]
    public async Task Data_older_than_its_kinds_retention_is_deleted()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "old", "ann");
        await StoreRunAsync(storage, "acme", "long", "ann");
        await storage.Records.TryAppendAsync("acme", Fact("long", 2, Start.AddDays(20)), Ct);
        await storage.Tasks.TryAppendAsync("acme", Change("long", 2, Start.AddDays(20)), Ct);
        await storage.Checkpoints.AppendAsync("acme", Saved("long", 1) with { Time = Start.AddDays(20) }, Ct);
        await StoreRunAsync(storage, "acme", "new", "ann", Start.AddDays(20));
        var retention = new RetentionOptions
        {
            Events = TimeSpan.FromDays(10), Conversations = TimeSpan.FromDays(10), Audit = TimeSpan.FromDays(30), RunRecords = TimeSpan.FromDays(10),
            Artifacts = TimeSpan.FromDays(10), TaskBoards = TimeSpan.FromDays(10),
        };

        await storage.DeleteExpiredAsync(retention, Start.AddDays(25), Ct);

        var export = await storage.ExportAsync("acme", "ann", Ct);
        Assert.Equal(["old", "long", "new"], export.Runs.Select(run => run.RunId));
        Assert.Equal(["new"], export.Events.Select(kept => kept.RunId));
        Assert.Equal(["old", "long", "new"], export.Audit.Select(entry => entry.RunId));
        Assert.Equal([Start.AddDays(20)], export.Conversations.Select(turn => turn.Time));
        Assert.Equal([("long", 1L), ("long", 2L), ("new", 1L)], export.Records.Select(entry => (entry.RunId, entry.Revision)).Order());
        Assert.Equal(["new result"], export.Artifacts.Select(artifact => artifact.Name));
        Assert.Equal([("long", 1L), ("long", 2L), ("new", 1L)], export.Tasks.Select(change => (change.RunId, change.Revision)).Order());
        Assert.Equal(["long", "long", "new"], export.Checkpoints.Select(checkpoint => checkpoint.RunId).Order());
    }

    protected static Checkpoint Saved(string runId, int number, DateTimeOffset? time = null) =>
        new(runId, number, time ?? Start, CheckpointPoint.Turn, new Dictionary<string, int> { ["extractor"] = 3 }, 4, 5, 6, 7, [new CopySnapshot("run-dev", "dev", "abc123")]);

    protected static RunStarted Run(string runId, string? owner, DateTimeOffset? time = null) =>
        new(runId, "extractor", owner, time ?? Start, CoreVersion.Value, new OfficinaOptions());

    protected static CoreEvent Event(string runId, long sequence, DateTimeOffset? time = null) =>
        new(runId, "extractor", null, sequence, time ?? Start, new ModelCallEnded(StopReason.Finished, new Usage(10, 2, 0, 0), 0.5m));

    protected static AuditEntry Entry(string runId, AuditOutcome outcome, DateTimeOffset? time = null) =>
        new(runId, "extractor", "ann", "create_issue", """{"title":"Crash"}""", null, outcome, time ?? Start, "key");

    protected static ConversationTurn Turn(string owner, params Message[] messages) => new("extractor", owner, Start, [.. messages], Shortened: false);

    protected static RecordEntry Fact(string runId, long revision, DateTimeOffset? time = null) =>
        new(runId, revision, "extractor", time ?? Start, new Fact("invoice.total", "42", "invoice.pdf", null));

    protected static MemoryChange Proposed(string scope, long revision) =>
        new(scope, revision, "dev", Start, MemoryAction.Proposed, 1, new MemoryProposal(MemoryKind.Decision, "build", "Use MSBuild.", "It is what we have.", [3]), null);

    protected static MemoryChange Approved(string scope, long revision) => new(scope, revision, "lead", Start, MemoryAction.Approved, 1, null, "Agreed.");

    protected static TaskChange Change(string runId, long revision, DateTimeOffset? time = null) =>
        new(runId, revision, "lead", time ?? Start, "t1 added as Ready", "planned",
            [new BoardTask { Id = "t1", Title = "Fix the parser", Checks = ["tests"], DependsOn = ["t0"], Budget = 8, State = TaskState.Ready }]);

    /// <summary>
    /// A run with one event, one audit entry, one turn of the owner's conversation, a record of one fact, one artifact and a
    /// task board of one change, all at <paramref name="time"/>.
    /// </summary>
    private static async Task StoreRunAsync(IStorage storage, string tenant, string runId, string owner, DateTimeOffset? time = null)
    {
        await storage.Runs.RecordStartAsync(tenant, Run(runId, owner, time), Ct);
        await storage.Events.AppendAsync(tenant, Event(runId, 1, time), Ct);
        await storage.Audit.AppendAsync(tenant, Entry(runId, AuditOutcome.Intent, time), Ct);
        await storage.Conversations.AppendAsync(tenant, Turn(owner, Message.User(runId)) with { Time = time ?? Start }, Ct);
        await storage.Records.TryAppendAsync(tenant, Fact(runId, 1, time), Ct);
        await storage.Artifacts.SaveAsync(tenant, runId, new($"{runId} result", "full text"), time ?? Start, Ct);
        await storage.Tasks.TryAppendAsync(tenant, Change(runId, 1, time), Ct);
        await storage.Memory.TryAppendAsync(tenant, Proposed($"owner:{owner}", 1), Ct);
        await storage.Checkpoints.AppendAsync(tenant, Saved(runId, 0, time), Ct);
    }

    /// <summary>How many items an export of the owner's data holds.</summary>
    private static async Task<int> CountAsync(IStorage storage, string? tenant, string owner)
    {
        var data = await storage.ExportAsync(tenant, owner, Ct);
        return data.Runs.Count + data.Events.Count + data.Audit.Count + data.Conversations.Count + data.Records.Count + data.Artifacts.Count + data.Tasks.Count + data.Memory.Count + data.Checkpoints.Count;
    }

    private static JsonElement Args => JsonDocument.Parse("""{ "path": "a.cs" }""").RootElement;

    private static string Json(OfficinaOptions options) => JsonSerializer.Serialize(options, ConfigurationJson.Options);

    /// <summary>Changes compare by their JSON: their lists of tasks are new on every read.</summary>
    private static string Json(params TaskChange[] changes) => JsonSerializer.Serialize(changes);

    private static string Json(params MemoryChange[] changes) => JsonSerializer.Serialize(changes);
}

public sealed class InMemoryStorageTests : StorageContract
{
    protected override Task<IStorage> CreateAsync() => Task.FromResult<IStorage>(new InMemoryStorage());
}
