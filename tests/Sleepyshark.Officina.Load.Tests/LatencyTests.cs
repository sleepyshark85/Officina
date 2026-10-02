using System.Diagnostics;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Storage.Sqlite;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Load.Tests;

/// <summary>What the core adds to each iteration of a turn (LAT-01) and to the first streamed text (LAT-02).</summary>
[Collection(nameof(Load))]
public class LatencyTests
{
    private const int Iterations = 20;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // LAT-01, TEST-30: excluding model time, tool time and durable storage writes, the core adds less than 5 ms per iteration at the
    // 95th percentile. An iteration is everything from one model call to the next: the reply's events, the tool pipeline with its
    // gates and audit, the events, the history and the next request. The tools return at once, storage is in memory, and the time
    // spent inside the model provider is taken out.
    [Fact]
    public async Task The_core_adds_less_than_5_ms_per_iteration_at_the_95th_percentile()
    {
        var samples = await IterationTimesAsync(new InMemoryStorage(), conversations: false, runs: 60);

        Assert.True(Measure.Report("LAT-01 core time per iteration", samples) < 5, "The core adds 5 ms or more per iteration at the 95th percentile.");
    }

    // LAT-02, TEST-30: for an interactive trigger, the first streamed text reaches the caller as soon as the provider sends it; the
    // core adds less than 50 ms. The caller reads the run's events as `sof run` does, on SQLite storage as with `sof`. Streamed text
    // is not stored by default (storage.unstoredEvents), so no durable write is on its path: this is one interactive agent with
    // nothing else writing. In a team, a text delta can wait behind the other agents' durable writes (a follow-up in S21).
    [Fact]
    public async Task The_first_streamed_text_reaches_the_caller_within_50_ms_of_the_provider_sending_it()
    {
        var model = new ScriptedModelProvider();
        var timed = new TimedProvider(model);
        var folder = Directory.CreateTempSubdirectory("officina-load-");
        try
        {
            var runner = Measure.Runner(Chat(), timed, await SqliteStorage.OpenAsync(Path.Combine(folder.FullName, "sof.db"), Ct));
            var samples = new List<double>();
            for (var run = 0; run < 120; run++)
            {
                model.Reply(new TextDelta("The first words"), new TextDelta(" and the rest."), new Stopped(StopReason.Finished));
                timed.TextSent.Clear();
                var work = new Work("worker", "Say something.");
                var reading = FirstTextAsync(runner, work.RunId);
                Assert.Equal(AgentOutcome.Completed, (await runner.RunAsync(work, Ct)).Outcome);
                var received = await reading.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                if (run >= 20)
                {
                    samples.Add(Measure.Milliseconds(received - timed.TextSent.First()));
                }
            }

            Assert.True(Measure.Report("LAT-02 provider to caller, first text", samples) < 50, "The core adds 50 ms or more to the first streamed text.");
            Assert.True(Measure.Percentile(samples, 99) < 50, "The core adds 50 ms or more to the first streamed text, at the 99th percentile.");
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    // TEST-30: durable storage writes are measured on their own. The same turns run on SQLite, as with `sof`, with the conversation
    // store on; each write of an event, an audit entry and a conversation turn is timed, and so is the iteration with them.
    [Fact]
    public async Task Durable_storage_writes_are_measured_on_their_own()
    {
        var folder = Directory.CreateTempSubdirectory("officina-load-");
        try
        {
            var storage = new TimedStorage(await SqliteStorage.OpenAsync(Path.Combine(folder.FullName, "sof.db"), Ct));
            var iterations = await IterationTimesAsync(storage, conversations: true, runs: 25);

            Measure.Report("Core time per iteration with SQLite storage", iterations);
            foreach (var kind in storage.Writes.GroupBy(write => write.Kind))
            {
                Measure.Report($"SQLite write of a {kind.Key}", [.. kind.Select(write => write.Milliseconds)]);
            }

            Assert.Equal(["audit entry", "conversation turn", "event"], storage.Writes.Select(write => write.Kind).Distinct().Order(StringComparer.Ordinal));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Runs turns of <see cref="Iterations"/> model calls, each but the last asking for a tool that reads or writes, and returns the
    /// core's time in each iteration once warmed up: from the start to the first model call, between calls less the time inside the
    /// model, and from the last call to the end. Each run has a caller, and so a conversation, of its own.
    /// </summary>
    private static async Task<List<double>> IterationTimesAsync(IStorage storage, bool conversations, int runs)
    {
        var model = new ScriptedModelProvider();
        var timed = new TimedProvider(model);
        var tools = new Dictionary<string, ITool> { ["read"] = new FakeTool(ToolKind.Read), ["write"] = new FakeTool(ToolKind.Write) };
        var options = WithTools();
        if (conversations)
        {
            var worker = options.Agents["worker"];
            options = options with
            {
                Capabilities = options.Capabilities with { ConversationStore = new() { Enabled = true } },
                Agents = new Dictionary<string, AgentDefinition>
                {
                    ["worker"] = worker with { Context = worker.Context with { History = new() { Strategy = HistoryStrategy.Full } } },
                },
            };
        }
        var runner = Measure.Runner(options, timed, storage, tools);
        var samples = new List<double>();
        for (var run = 0; run < runs; run++)
        {
            for (var call = 1; call < Iterations; call++)
            {
                model.CallTools((call % 2 == 0 ? "write" : "read", $$"""{ "n": {{call}} }"""));
            }

            model.Reply("Done.");
            timed.Calls.Clear();
            var work = new Work("worker", "Work.") { Caller = new($"caller-{run}", null, new HashSet<string>(), new Dictionary<string, string>()) };
            var started = Stopwatch.GetTimestamp();
            var result = await runner.RunAsync(work, Ct);
            var ended = Stopwatch.GetTimestamp();

            Assert.Equal(AgentOutcome.Completed, result.Outcome);
            var calls = timed.Calls.OrderBy(call => call.Started).ToArray();
            Assert.Equal(Iterations, calls.Length);
            if (run < 10)
            {
                continue; // warming up: the JIT, the caches
            }

            samples.Add(Measure.Milliseconds(calls[0].Started - started));
            for (var i = 0; i < calls.Length; i++)
            {
                var next = i + 1 < calls.Length ? calls[i + 1].Started : ended;
                samples.Add(Measure.Milliseconds(next - calls[i].Started - calls[i].Inside));
            }
        }

        return samples;
    }

    /// <summary>When the caller, reading the run's events, receives its first text.</summary>
    private static async Task<long> FirstTextAsync(AgentRunner runner, string runId)
    {
        await foreach (var coreEvent in runner.Events.ReadAsync(null, runId, 0, Ct))
        {
            if (coreEvent.Payload is TextGenerated)
            {
                return Stopwatch.GetTimestamp();
            }
        }

        throw new InvalidOperationException("The run ended without text.");
    }

    /// <summary>An agent with a tool that reads and one that writes, behind a gate, in the auto mode, so no one is asked.</summary>
    private static OfficinaOptions WithTools() => new()
    {
        Run = new() { PermissionMode = PermissionMode.Auto },
        Agents = new Dictionary<string, AgentDefinition> { ["worker"] = new() { Instructions = "Work.", Tools = ["work"] } },
        Tools = new Dictionary<string, ToolOptions>
        {
            ["read"] = new() { Source = "extension:read" },
            ["write"] = new() { Source = "extension:write", Gates = ["untrusted"] },
        },
        ToolSets = new Dictionary<string, IReadOnlyList<string>> { ["work"] = ["read", "write"] },
        Gates = new Dictionary<string, GateOptions> { ["untrusted"] = new() { Use = GateOptions.UntrustedContentApproval } },
    };

    private static OfficinaOptions Chat() => new()
    {
        Agents = new Dictionary<string, AgentDefinition> { ["worker"] = new() { Instructions = "Answer." } },
    };
}
