using System.ComponentModel;
using Sleepyshark.Officina;
using Sleepyshark.Officina.Mcp;

namespace Samples.BackgroundAgent;

/// <summary>How a job left its ticket.</summary>
public enum Resolution
{
    Answered,
    Escalated,
}

/// <summary>What a job reports about its ticket: its typed output.</summary>
public sealed record TicketOutcome(
    [property: Description("Answered when the note answers the customer; escalated when a person must act.")] Resolution Resolution,
    [property: Description("One sentence for the support team on what was done and why.")] string Report);

/// <summary>
/// A background agent: one job per support ticket, started by a queue or schedule, with nobody to ask. Without an
/// approver, calls needing approval are denied and the model is told. Its tools are the helpdesk's MCP server, over
/// Streamable HTTP, plus the app's own refund tool. It audits to a JSON-lines file, each job has a budget, and it returns
/// typed output beside its side effect, a note on the ticket. Each job is one stateless run, on a conversation named
/// after its ticket; it needs no memory, as each job starts afresh.
/// </summary>
public sealed class TicketJob : IAsyncDisposable
{
    /// <summary>What one job may spend.</summary>
    public static readonly Budget PerJob = new() { Cost = 0.20m, Tokens = 100_000, ModelCalls = 6, Time = TimeSpan.FromMinutes(2) };

    private readonly McpToolSource helpdesk;
    private readonly JsonLinesAuditSink audit;

    private TicketJob(McpToolSource helpdesk, JsonLinesAuditSink audit, Agent agent) => (this.helpdesk, this.audit, Agent) = (helpdesk, audit, agent);

    public Agent Agent { get; }

    /// <summary>
    /// Connects to the helpdesk at <paramref name="helpdeskUrl"/> with <paramref name="token"/> and builds the agent.
    /// <paramref name="refund"/> issues a refund for a ticket id and amount, and returns a receipt.
    /// </summary>
    public static async Task<TicketJob> StartAsync(
        IModel model, Uri helpdeskUrl, string token, Func<string, decimal, string> refund, string auditFile, CancellationToken cancellationToken = default)
    {
        var server = McpServer.Http("helpdesk", helpdeskUrl, new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" });

        // The host decides what each of the server's tools may do, rather than trusting its annotations.
        var helpdesk = await McpToolSource.ConnectAsync(
            server, [new AllowedTool("get_ticket", ToolKind.Read), new AllowedTool("add_note", ToolKind.Write)], cancellationToken).ConfigureAwait(false);
        try
        {
            var refundTool = Tool.FromFunction(
                "issue_refund",
                "Refunds a customer for a ticket. A person must approve each refund.",
                ToolKind.Write,
                ([Description("The ticket's id.")] string ticketId, [Description("The amount, in pounds.")] decimal amount) => refund(ticketId, amount),
                needsApproval: true);
            var agent = new Agent
            {
                Name = "ticket-job",
                Model = model,
                Instructions = Instructions,
                Tools = [.. helpdesk.Tools, refundTool],
                Output = OutputContract.For<TicketOutcome>(),
                Secrets = [token],
            };
            var audit = new JsonLinesAuditSink(auditFile);
            return new TicketJob(helpdesk, audit, agent with { AuditSink = audit });
        }
        catch
        {
            // Nothing else holds the connection yet, so it is closed here.
            await helpdesk.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Handles one ticket; the result says how the run ended, and on completion holds the <see cref="TicketOutcome"/>.</summary>
    public Task<RunResult> HandleAsync(string ticketId, CancellationToken cancellationToken = default) =>
        Agent.RunAsync(new Conversation { Id = $"ticket-{ticketId}" }, $"Handle ticket {ticketId}.", new() { Budget = PerJob }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await helpdesk.DisposeAsync().ConfigureAwait(false);
        audit.Dispose();
    }

    private const string Instructions = """
        You work through an online shop's support tickets on your own, with nobody to ask. The user message names one
        ticket. Read it with the helpdesk tools, do what you can, and leave a note on the ticket for the customer or
        the support team. A refund needs a person's approval: if it is denied, escalate the ticket instead, and say so
        in the note. Reply with the outcome only.
        """;
}
