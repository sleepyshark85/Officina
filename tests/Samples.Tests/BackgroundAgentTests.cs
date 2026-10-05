using System.Text.Json;
using Samples.BackgroundAgent;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Testing;

namespace Samples.Tests;

/// <summary>
/// GEN-06: the background agent sample, offline: unattended, with the helpdesk a fake MCP server over Streamable HTTP
/// and the audit trail a JSON-lines file. Only the model, the MCP server and the refund back end are replaced.
/// </summary>
public sealed class BackgroundAgentTests : IDisposable
{
    private const string Token = "hd-token-5e1f";

    private static readonly ModelPrice Price = new(Input: 5m, Output: 25m, CacheRead: 0.5m, CacheWrite: 6.25m, CacheWriteHour: 10m);

    private readonly string auditFile = Path.Combine(Path.GetTempPath(), $"officina-sample-audit-{Guid.NewGuid():N}.jsonl");
    private readonly List<(string Ticket, decimal Amount)> refunds = [];
    private readonly FakeMcpServer helpdesk = new(
        new FakeMcpTool("get_ticket", input => $"Ticket {input.GetProperty("id").GetString()} from Ana: order A-1042 arrived broken, she wants her £25 back.")
        {
            InputSchema = """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
        },
        new FakeMcpTool("add_note", input => $"Note added to {input.GetProperty("id").GetString()}.")
        {
            InputSchema = """{"type":"object","properties":{"id":{"type":"string"},"note":{"type":"string"}},"required":["id","note"]}""",
        })
    {
        Token = Token,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        helpdesk.Dispose();
        File.Delete(auditFile);
    }

    private static ToolCall Call(string id, string name, object input) => new(id, name, JsonSerializer.Serialize(input));

    private Task<TicketJob> StartAsync(ScriptedModel model) =>
        TicketJob.StartAsync(model, helpdesk.StartHttp(), Token, (ticket, amount) => { refunds.Add((ticket, amount)); return "refunded"; }, auditFile, Ct);

    [Fact]
    public async Task An_unattended_job_reads_the_ticket_is_denied_the_refund_escalates_with_a_note_and_records_it_all()
    {
        var model = new ScriptedModel { Price = Price }
            .CallTools(Call("c1", "helpdesk__get_ticket", new { id = "T-7" }))
            .CallTools(Call("c2", "issue_refund", new { ticketId = "T-7", amount = 25m }))
            .CallTools(Call("c3", "helpdesk__add_note", new { id = "T-7", note = "A refund of £25 needs a person's approval; escalated." }))
            .Reply("""{"resolution":"Escalated","report":"Ana asks for £25 back for a broken order; a refund needs a person's approval."}""");
        await using var job = await StartAsync(model);

        var result = await job.HandleAsync("T-7", Ct);

        var outcome = Assert.IsType<TicketOutcome>(Assert.IsType<Completed>(result).Output);
        Assert.Equal(Resolution.Escalated, outcome.Resolution);

        // Nobody could approve the refund, so it never ran, and the model was told why (GEN-04).
        Assert.Empty(refunds);
        Assert.Equal(
            new ToolResult("c2", "The call needs approval, and this run is unattended, so it was denied.", true),
            model.Requests[2].Messages[^1].Blocks[0].ToolResult);

        // The helpdesk's tools ran on the server, over HTTP with the token (MCP-01).
        Assert.Equal(["get_ticket", "add_note"], helpdesk.Calls.Select(call => call.Tool));

        // The JSON-lines file holds the run's record: the server connected, the write audited before it ran, the end.
        var entries = File.ReadAllLines(auditFile).Select(line => JsonDocument.Parse(line).RootElement).ToList();
        string Kind(JsonElement entry) => entry.GetProperty("kind").GetString()!;
        string? Tool(JsonElement entry) => entry.TryGetProperty("tool", out var tool) ? tool.GetString() : null;
        Assert.Equal("RunStarted", Kind(entries[0]));
        Assert.Equal(("ToolSource", "helpdesk"), (Kind(entries[1]), Tool(entries[1])));
        Assert.Equal(
            ["ToolStarted", "ToolEnded"],
            entries.Where(entry => Tool(entry) == "helpdesk__add_note").Select(Kind));
        Assert.Equal("error", entries.Single(entry => Tool(entry) == "issue_refund").GetProperty("outcome").GetString());
        Assert.Equal(("RunEnded", "Completed"), (Kind(entries[^1]), entries[^1].GetProperty("outcome").GetString()));
        Assert.All(entries, entry => Assert.Equal("ticket-T-7", entry.GetProperty("conversation").GetString()));
        Assert.DoesNotContain(Token, File.ReadAllText(auditFile), StringComparison.Ordinal);

        Assert.Empty(PrefixStability.Problems(model.Requests));
    }

    [Fact]
    public async Task A_job_that_keeps_calling_tools_stops_at_its_budget()
    {
        var model = new ScriptedModel { Price = Price };
        for (var call = 1; call <= TicketJob.PerJob.ModelCalls; call++)
        {
            model.CallTools(Call($"c{call}", "helpdesk__get_ticket", new { id = "T-8" }));
        }

        await using var job = await StartAsync(model);

        var result = await job.HandleAsync("T-8", Ct);

        var stopped = Assert.IsType<Stopped>(result);
        Assert.Equal(StopReason.Budget, stopped.Reason);
        Assert.Equal("The model call budget is used up: 6 of 6.", stopped.Detail);
        Assert.Equal(TicketJob.PerJob.ModelCalls, model.Requests.Count);
        Assert.Empty(PrefixStability.Problems(model.Requests));
    }
}
