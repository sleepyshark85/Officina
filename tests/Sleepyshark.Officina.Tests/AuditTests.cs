using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Audit.JsonLines;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>The audit trail (AUD-01…05).</summary>
public class AuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_run_records_its_start_its_tool_calls_its_approvals_and_its_end_in_sequence()
    {
        var time = new FakeTimeProvider(Start);
        var sink = new RecordingSink();
        var model = new ScriptedModel()
            .CallTools(new ToolCall("c1", "search", """{"query":"x"}"""), new ToolCall("c2", "order", "{}"))
            .Reply(new BlockReceived(ScriptedModel.TextBlock("Done.")), new UsageReceived(new Usage(10, 2, 0, 0)), new ModelStopped(ModelStopReason.End));
        var order = Agents.Tool("order", kind: ToolKind.Write, needsApproval: true, handler: (_, _) =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return Task.FromResult(new ToolOutput("ordered"));
        });
        var agent = Agents.With(model, tools: [Agents.SearchTool(), order]) with
        {
            Name = "bookshop",
            AuditSink = sink,
            Time = time,
            Approver = new ScriptedApprover().Answer(Approval.Granted),
        };
        var conversation = new Conversation { Id = "session-1" };

        await agent.RunAsync(conversation, "Order x.", cancellationToken: Ct);

        var entries = sink.Entries;
        Assert.Equal(
            [
                (AuditKind.RunStarted, null, null), (AuditKind.ToolStarted, "search", null), (AuditKind.ToolEnded, "search", "ok"),
                (AuditKind.ApprovalAsked, "order", null), (AuditKind.ApprovalAnswered, "order", "approved"), (AuditKind.ToolStarted, "order", null),
                (AuditKind.ToolEnded, "order", "ok"), (AuditKind.RunEnded, null, "Completed"),
            ],
            entries.Select(entry => (entry.Kind, entry.Tool, entry.Outcome)));
        Assert.Equal(Enumerable.Range(1, entries.Count).Select(number => (long)number), entries.Select(entry => entry.Sequence));
        Assert.Single(entries.Select(entry => entry.Run).Distinct());
        Assert.All(entries, entry => Assert.Equal(("session-1", "bookshop", (string?)null), (entry.Conversation, entry.Agent, entry.MemoryScope)));
        Assert.Equal(Start, entries[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(2), entries[6].Duration);
        Assert.Equal(("ordered", "{}"), (entries[6].Detail, entries[6].Input));
        Assert.Equal(new Usage(10, 2, 0, 0), entries[^1].Usage);
    }

    [Fact]
    public async Task A_write_runs_only_after_its_attempt_is_recorded()
    {
        var sink = new RecordingSink();
        var recordedFirst = false;
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "save", "{}")).Reply("Saved.");
        var save = Agents.Tool("save", kind: ToolKind.Write, handler: (_, _) =>
        {
            recordedFirst = sink.Entries.Any(entry => entry is { Kind: AuditKind.ToolStarted, CallId: "c1" });
            return Task.FromResult(new ToolOutput("saved"));
        });

        await (Agents.With(model, tools: save) with { AuditSink = sink }).RunAsync(new Conversation(), "Save.", cancellationToken: Ct);

        Assert.True(recordedFirst);
    }

    [Fact]
    public async Task A_write_whose_attempt_cannot_be_audited_never_runs_and_the_model_is_told_while_a_read_still_runs()
    {
        var ran = new List<string>();
        var sink = new RecordingSink(entry => entry.Kind == AuditKind.ToolStarted);
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "save", "{}"), new ToolCall("c2", "look", "{}")).Reply("The save failed.");
        Tool Recording(string name, ToolKind kind) => Agents.Tool(name, kind: kind, handler: (_, _) =>
        {
            lock (ran)
            {
                ran.Add(name);
            }

            return Task.FromResult(new ToolOutput("ok"));
        });
        var conversation = new Conversation();

        var result = await (Agents.With(model, tools: [Recording("save", ToolKind.Write), Recording("look", ToolKind.Read)]) with { AuditSink = sink })
            .RunAsync(conversation, "Save.", cancellationToken: Ct);

        Assert.Equal("The save failed.", Assert.IsType<Completed>(result).Text);
        Assert.Equal(["look"], ran);
        Assert.Equal(
            new ToolResult("c1", "The call was not run: its attempt could not be recorded in the audit trail.", true),
            conversation.Messages[2].Blocks[0].ToolResult);
    }

    [Fact]
    public async Task Long_text_is_truncated_with_its_size_and_secrets_are_redacted()
    {
        var sink = new RecordingSink();
        var input = JsonSerializer.Serialize(new { query = "password=hunter2 " + new string('x', 5_000) });
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "search", input)).Reply("Done.");
        var search = Agents.Tool("search", schema: Agents.SearchSchema, handler: (_, _) => throw new InvalidOperationException("Login failed for hunter2."));

        await (Agents.With(model, tools: search) with { AuditSink = sink, Secrets = ["hunter2"] }).RunAsync(new Conversation(), "Go.", cancellationToken: Ct);

        var started = sink.Entries.Single(entry => entry.Kind == AuditKind.ToolStarted);
        Assert.StartsWith("""{"query":"password=[redacted] xxx""", started.Input, StringComparison.Ordinal);
        Assert.EndsWith($"… [truncated: {input.Length - "hunter2".Length + "[redacted]".Length} characters]", started.Input, StringComparison.Ordinal);
        Assert.Equal(AuditRecorder.MaxTextLength + $"… [truncated: {input.Length + 3} characters]".Length, started.Input!.Length);
        Assert.Equal(("error", "Login failed for [redacted]."), sink.Entries.Where(entry => entry.Kind == AuditKind.ToolEnded).Select(entry => (entry.Outcome, entry.Detail)).Single());
        Assert.DoesNotContain(sink.Entries, entry => $"{entry.Input}{entry.Detail}".Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entry_the_sink_failed_to_write_leaves_a_gap_in_the_sequence()
    {
        var sink = new RecordingSink(entry => entry.Kind == AuditKind.ToolStarted);
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "look", "{}")).Reply("Done.");

        await (Agents.With(model, tools: Agents.Tool("look")) with { AuditSink = sink }).RunAsync(new Conversation(), "Look.", cancellationToken: Ct);

        Assert.Equal([1L, 3, 4], sink.Entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task A_secret_is_redacted_as_escaped_in_JSON_too_and_truncation_keeps_surrogate_pairs_whole()
    {
        var sink = new RecordingSink();
        var secret = "pa\"ss\\word";
        // The redacted input puts the first emoji's high surrogate last in the kept text, so the cut must leave it out.
        var input = """{"query":"pa\"ss\\word """ + new string('x', AuditRecorder.MaxTextLength - 22) + "😀😀\"}";
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "search", input)).Reply("Done.");

        await (Agents.With(model, tools: Agents.SearchTool()) with { AuditSink = sink, Secrets = [secret] }).RunAsync(new Conversation(), "Go.", cancellationToken: Ct);

        var started = sink.Entries.Single(entry => entry.Kind == AuditKind.ToolStarted).Input!;
        Assert.StartsWith("""{"query":"[redacted] xxx""", started, StringComparison.Ordinal);
        var kept = started[..started.IndexOf('…', StringComparison.Ordinal)];
        Assert.False(char.IsHighSurrogate(kept[^1]));
        Assert.Equal(AuditRecorder.MaxTextLength - 1, kept.Length);
    }

    [Fact]
    public async Task Refusals_failures_prefix_mismatches_and_budget_stops_are_recorded_as_the_run_ends()
    {
        var sink = new RecordingSink();
        var model = new ScriptedModel().Reply(new BlockReceived(ScriptedModel.TextBlock("No.")), new ModelStopped(ModelStopReason.Refusal, "cyber")).Fail(new InvalidOperationException("Overloaded."));
        var agent = Agents.With(model) with { AuditSink = sink };
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "Hack it.", cancellationToken: Ct);
        await agent.RunAsync(conversation, "Again.", cancellationToken: Ct);
        await (agent with { Instructions = "Changed." }).RunAsync(conversation, "Again.", cancellationToken: Ct);
        await agent.RunAsync(conversation, "Again.", new() { Budget = new Budget { ModelCalls = 0 } }, Ct);

        Assert.Equal(
            [
                ("Stopped: Refusal", "cyber"), ("Failed: ModelError", "Overloaded."),
                ("Failed: PrefixMismatch", "The agent's tools, instructions or model settings differ from those this conversation was started with. Start a new conversation."),
                ("Stopped: Budget", "The model call budget is used up: 0 of 0."),
            ],
            sink.Entries.Where(entry => entry.Kind == AuditKind.RunEnded).Select(entry => (entry.Outcome!, entry.Detail!)));
    }

    [Fact]
    public async Task The_JSON_lines_sink_appends_one_entry_per_line()
    {
        var path = Path.Combine(Path.GetTempPath(), $"officina-audit-{Guid.NewGuid():N}.jsonl");
        try
        {
            using var telemetry = new TelemetryCollector(); // so the entries carry their trace and span, whatever other tests do
            using var sink = new JsonLinesAuditSink(path);
            var model = new ScriptedModel().CallTools(new ToolCall("c1", "save", "{}")).Reply("Saved.");
            var agent = Agents.With(model, tools: Agents.Tool("save", kind: ToolKind.Write)) with { AuditSink = sink, Time = new FakeTimeProvider(Start) };

            await agent.RunAsync(new Conversation { Id = "s1" }, "Save.", cancellationToken: Ct);

            var lines = await File.ReadAllLinesAsync(path, Ct);
            Assert.Equal(4, lines.Length);
            Assert.Equal(
                """{"time":"2026-10-05T09:00:00+00:00","sequence":2,"run":"ID","conversation":"s1","agent":"agent","traceId":"ID","spanId":"ID","kind":"ToolStarted","tool":"save","callId":"c1","input":"{}"}""",
                System.Text.RegularExpressions.Regex.Replace(lines[1], "\"(run|traceId|spanId)\":\"[0-9a-f]+\"", "\"$1\":\"ID\""));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_JSON_lines_sink_reports_a_failed_write()
    {
        using var sink = new JsonLinesAuditSink(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "audit.jsonl"));
        var entry = new AuditEntry { Time = Start, Sequence = 1, Run = "r", Conversation = "c", Agent = "a", Kind = AuditKind.RunStarted };

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => sink.WriteAsync(entry, Ct));
    }

    [Fact]
    public async Task A_tool_source_that_cannot_report_its_changes_is_audited_as_failed_and_the_run_still_ends()
    {
        var sink = new RecordingSink();
        var source = new SilentSource();
        var lookup = new Tool("lookup", "Looks up.", """{"type":"object"}""", ToolKind.Read, (_, _) => Task.FromResult(new ToolOutput("found"))) { Source = source };
        var model = new ScriptedModel().CallTools(new ToolCall("c1", "lookup", "{}")).Reply("Found it.");

        var result = await (Agents.With(model, tools: lookup) with { AuditSink = sink }).RunAsync("Find it.", cancellationToken: Ct);

        Assert.Equal("Found it.", Assert.IsType<Completed>(result).Text);
        Assert.Equal(
            [("silent", "failed", "The tool source 'silent' could not report its connection changes: The change log is unreadable.")],
            sink.Entries.Where(entry => entry.Kind == AuditKind.ToolSource).Select(entry => (entry.Tool, entry.Outcome, entry.Detail)).Distinct());
    }

    /// <summary>A tool source, such as an MCP server, that connects but fails whenever it is asked for its changes.</summary>
    private sealed class SilentSource : IToolSource
    {
        public string Name => "silent";

        public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IReadOnlyList<ToolSourceChange> TakeChanges() => throw new InvalidOperationException("The change log is unreadable.");
    }
}
