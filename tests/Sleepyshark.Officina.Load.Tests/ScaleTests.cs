using System.Diagnostics;
using System.Globalization;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Core.Tasks;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Load.Tests;

/// <summary>How much one run and one process carry (SCALE-01, SCALE-02), on SQLite storage as with <c>sof</c>.</summary>
[Collection(nameof(Load))]
public class ScaleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // SCALE-02, TEST-30: one run supports at least 8 agents working at once and 500 tasks on one developer machine. The lead plans
    // 500 tasks; 8 developers take them, the first 8 held until all 8 work at once, and each submits its task, which its checks
    // pass, so it is done. SCALE-03 (a SHOULD): as the run goes on, it keeps its pace; the last 100 tasks take no longer than half
    // as much again as the second 100. The run is SQLite's, as with `sof`.
    [Fact]
    public async Task One_run_supports_8_agents_working_at_once_and_500_tasks_at_a_steady_pace()
    {
        const int Tasks = 500, Agents = 8;
        using var together = new Barrier(Agents);
        var holding = 0;
        var tools = new Dictionary<string, ITool>
        {
            ["hold"] = new FakeTool(ToolKind.Read, run: async (_, ct) =>
            {
                // The first 8 tasks wait here until 8 agents work at once; the rest go straight on.
                if (Interlocked.Increment(ref holding) <= Agents)
                {
                    await Task.Run(() => together.SignalAndWait(TimeSpan.FromMinutes(1), ct), ct);
                }

                return ToolResult.Success("held");
            }),
        };
        var model = new ScriptedModelProvider();
        var lead = model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("You lead a team", StringComparison.Ordinal));
        foreach (var batch in Enumerable.Range(1, Tasks).Chunk(100))
        {
            lead.CallTools([.. batch.Select(n => ("create", $$"""{ "id": "t{{n}}", "title": "Task {{n}}", "role": "developer", "reason": "plan" }"""))]);
        }

        lead.Reply("Planned.");
        model.When(request => ScriptedModelProvider.WorkOf(request).StartsWith("Every task is done", StringComparison.Ordinal)).Reply("All done.");
        foreach (var n in Enumerable.Range(1, Tasks))
        {
            model.When(request => ScriptedModelProvider.WorkOf(request).Contains($"Do task t{n},", StringComparison.Ordinal))
                .CallTools(("hold", "{}"), ("submit", $$"""{ "id": "t{{n}}" }""")).Reply("Submitted.");
        }

        var folder = Directory.CreateTempSubdirectory("officina-load-");
        try
        {
            var storage = await SqliteStorage.OpenAsync(Path.Combine(folder.FullName, "sof.db"), Ct);
            var runner = Measure.Runner(Team(Agents), model, storage, tools);
            var work = new Work("team", "Build it.");
            var started = Stopwatch.GetTimestamp();
            var result = await runner.RunAsync(work, Ct);
            var seconds = Measure.Milliseconds(Stopwatch.GetTimestamp() - started) / 1000;

            Assert.Equal((AgentOutcome.Completed, "All done."), (result.Outcome, result.Output));
            var tasks = await runner.Board(null, work.RunId).ReadAsync(Ct);
            Assert.Equal(Tasks, tasks.Count(task => task.State == TaskState.Done));
            var events = await ((IStorage)storage).Events.ReadAsync(null, work.RunId, 0, Ct);
            var peak = Peak(events);
            var done = events.Where(e => e.Payload is TaskStatusChanged { Status: TaskState.Done }).Select(e => e.Time).Order().ToList();
            var (second, last) = ((done[199] - done[100]).TotalSeconds, (done[^1] - done[^100]).TotalSeconds);
            Measure.Write(string.Create(CultureInfo.InvariantCulture,
                $"SCALE-02: {Tasks} tasks done by {Agents} developers, at most {peak} at once, in {seconds:0.0} s ({seconds * 1000 / Tasks:0.0} ms a task), {events.Count} events, {model.Requests.Count} model calls"));
            Measure.Write(string.Create(CultureInfo.InvariantCulture,
                $"SCALE-03: the second 100 tasks took {second:0.0} s, the last 100 {last:0.0} s; managed heap {GC.GetTotalMemory(forceFullCollection: true) / 1024 / 1024} MB at the end"));
            Assert.Equal(Agents, peak);
            Assert.True(last <= 1.5 * second + 1, "The run slowed down as it went on.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // SCALE-01 (a SHOULD, for the document Q&A application after v1), TEST-30: one process holds 1,000 concurrent short
    // conversations, each a caller's with the same agent. All start at once; then each goes on with a second turn, whose history is
    // restored from the conversation store, as the runner keeps no conversation in memory between turns. Turns of one agent run one
    // at a time (LOOP-02), so the conversations are served in turn. Storage is in memory: SQLite's writes are measured on their own.
    [Fact]
    public async Task One_process_holds_1000_concurrent_short_conversations_restored_from_the_store()
    {
        const int Conversations = 1000;
        var model = new ScriptedModelProvider();
        for (var reply = 0; reply < 2 * Conversations; reply++)
        {
            model.Reply("An answer.");
        }

        var options = new OfficinaOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new() { Instructions = "Answer.", Context = new() { History = new() { Strategy = HistoryStrategy.Full } } },
            },
            Capabilities = new() { ConversationStore = new() { Enabled = true } },
        };
        var runner = Measure.Runner(options, model, new InMemoryStorage());
        var callers = Enumerable.Range(0, Conversations).Select(n => new Caller($"caller-{n}", null, new HashSet<string>(), new Dictionary<string, string>())).ToList();

        var started = Stopwatch.GetTimestamp();
        var first = await Task.WhenAll(callers.Select(caller => runner.RunAsync("assistant", "A question.", caller, Ct)));
        var second = await Task.WhenAll(callers.Select(caller => runner.RunAsync("assistant", "Another question.", caller, Ct)));
        var seconds = Measure.Milliseconds(Stopwatch.GetTimestamp() - started) / 1000;

        Assert.All(first.Concat(second), result => Assert.Equal(AgentOutcome.Completed, result.Outcome));
        var resumed = model.Requests.Where(request => request.History.Length > 1).ToList();
        Assert.Equal(Conversations, resumed.Count);
        Assert.All(resumed, request => Assert.Equal(3, request.History.Length)); // the first question and answer, then the second question
        Measure.Write(string.Create(CultureInfo.InvariantCulture,
            $"SCALE-01: {Conversations} conversations of 2 turns in {seconds:0.0} s ({seconds * 1000 / (2 * Conversations):0.00} ms a turn), managed heap {GC.GetTotalMemory(forceFullCollection: true) / 1024 / 1024} MB after"));
    }

    /// <summary>The most agents that worked at once: each starts working with a status event and ends with its step.</summary>
    private static int Peak(IEnumerable<CoreEvent> events)
    {
        var (now, peak) = (0, 0);
        foreach (var payload in events.Select(coreEvent => coreEvent.Payload))
        {
            now += payload switch { AgentStatusChanged { Status: AgentStatus.Working } => 1, StepEnded => -1, _ => 0 };
            peak = Math.Max(peak, now);
        }

        return peak;
    }

    /// <summary>A team led by <c>lead</c>, which creates tasks, with up to <paramref name="developers"/> developers who hold and submit.</summary>
    private static OfficinaOptions Team(int developers) => new()
    {
        Agents = new Dictionary<string, AgentDefinition>
        {
            ["team"] = new()
            {
                Instructions = "A team.",
                Pattern = new()
                {
                    Type = PatternOptions.Team, Lead = "lead", MaxParallel = developers,
                    Roles = new Dictionary<string, RoleOptions> { ["developer"] = new() { Max = developers } },
                },
            },
            ["lead"] = new() { Instructions = "Lead.", Tools = ["lead"], Budget = new() { Turn = new() { ToolCalls = 1000 } } },
            ["developer"] = new() { Instructions = "Develop.", Tools = ["developer"] },
        },
        Tools = new Dictionary<string, ToolOptions>
        {
            ["create"] = new() { Source = "builtin:tasks.create" },
            ["submit"] = new() { Source = "builtin:tasks.submit_for_review" },
            ["hold"] = new() { Source = "extension:hold" },
        },
        ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["lead"] = ["create"], ["developer"] = ["submit", "hold"] },
        Capabilities = new() { TaskBoard = new() { Enabled = true }, Team = new() { Enabled = true } },
    };
}
