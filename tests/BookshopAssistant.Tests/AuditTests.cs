using Npgsql;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>The audit table sink against the real database, and <c>/audit</c> end to end (APP-16, AUD-03, TEST-09).</summary>
public class AuditTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task The_table_keeps_every_field_of_an_entry_and_reads_a_session_s_entries_in_order()
    {
        var table = new AuditTable(database.DataSource);
        var conversation = $"c-{Guid.NewGuid():N}";
        AuditEntry Entry(long sequence, AuditKind kind, string? conversationId = null) => new()
        {
            Time = Start.AddSeconds(sequence),
            Sequence = sequence,
            Run = "run-1",
            Conversation = conversationId ?? conversation,
            Agent = "bookshop",
            Kind = kind,
        };
        var full = Entry(2, AuditKind.ToolEnded) with
        {
            MemoryScope = "sam",
            TraceId = "4bf92f3577b34da6a3ce929d0e0e4736",
            SpanId = "00f067aa0ba902b7",
            Tool = "place_order",
            CallId = "c1",
            Input = """{"customerId":1}""",
            Outcome = "ok",
            Detail = "Placed order 81.",
            Duration = TimeSpan.FromMilliseconds(12.5),
            Usage = new Usage(1, 2, 3, 4),
            Cost = 0.0123m,
        };

        await table.WriteAsync(Entry(1, AuditKind.RunStarted), Ct);
        await table.WriteAsync(Entry(1, AuditKind.RunStarted, "another") with { Run = "run-2" }, Ct);
        await table.WriteAsync(full, Ct);

        Assert.Equal([Entry(1, AuditKind.RunStarted), full], await table.ReadAsync(conversation, Ct));
    }

    [DatabaseFact]
    public async Task A_write_the_database_refuses_throws()
    {
        var table = new AuditTable(database.DataSource);
        var entry = new AuditEntry { Time = Start, Sequence = 1, Run = "run-dup", Conversation = "c", Agent = "bookshop", Kind = AuditKind.RunStarted };
        await table.WriteAsync(entry, Ct);

        await Assert.ThrowsAsync<PostgresException>(() => table.WriteAsync(entry, Ct));
    }

    [DatabaseFact]
    public async Task APP_16_audit_shows_the_session_s_entries_grouped_by_run_with_approvals_tokens_and_a_link_to_each_run_s_trace()
    {
        using var telemetry = new TelemetryCollector();
        var model = Model()
            .Reply([.. SayThenCall("I'll add five copies.", Call("c1", "restock_book", new { bookId = 300, quantity = 5 }))[..^1],
                new UsageReceived(new Usage(100, 20, 1_000, 0)), new ModelStopped(ModelStopReason.ToolUse)])
            .Reply(new TextDelta("Done."), new BlockReceived(ScriptedModel.TextBlock("Done.")), new UsageReceived(new Usage(10, 5, 1_200, 0)), new ModelStopped(ModelStopReason.End))
            .Reply("Hello.");

        var transcript = await RunAsync(database, model, ["Sam", "Restock book 300 with 5.", "y", "Hi.", "/audit", "/audit nobody", "/quit"]);

        var conversation = await database.ScalarAsync<string>("select conversation from audit where tool = 'restock_book' limit 1");

        // Other test classes run the bookshop agent at the same time, so this session's runs are picked by their conversation.
        var traces = telemetry.Spans("bookshop")
            .Where(span => span.OperationName == "invoke_agent bookshop" && (string?)span.GetTagItem("gen_ai.conversation.id") == conversation)
            .Select(span => span.TraceId.ToHexString())
            .ToList();
        InOrder(
            transcript,
            "you> /audit\n",
            $"Audit of session {conversation}:\n",
            $"Run 1, trace: http://dashboard.test/traces/detail/{traces[0]}\n",
            "  08:00:00  RunStarted\n",
            "  08:00:00  ApprovalAsked     restock_book\n",
            "  08:00:00  ApprovalAnswered  restock_book          approved\n",
            "  08:00:00  ToolStarted       restock_book\n",
            "  08:00:00  ToolEnded         restock_book          ok  0 ms\n",
            "  08:00:00  RunEnded                                Completed  tokens: 2,310 in (2,200 cached), 25 out, $0.0014\n",
            $"Run 2, trace: http://dashboard.test/traces/detail/{traces[1]}\n",
            "  08:00:00  RunStarted\n",
            "  08:00:00  RunEnded                                Completed  tokens: 0 in (0 cached), 0 out, $0.0000\n",
            "you> /audit nobody\n",
            "No audit entries for session nobody.\n");
    }

    [DatabaseFact]
    public async Task APP_16_audit_with_an_id_shows_an_earlier_session()
    {
        var model = Model().Reply("Hello.");
        await RunAsync(database, model, ["Sam", "Hi.", "/quit"]);
        var earlier = await database.ScalarAsync<string>("select conversation from audit where kind = 'RunEnded' order by id desc limit 1");

        var transcript = await RunAsync(database, Model(), ["Sam", $"/audit {earlier}", "/quit"]);

        InOrder(transcript, $"Audit of session {earlier}:\n", "Run 1, trace: ", "RunStarted", "RunEnded");
    }
}
