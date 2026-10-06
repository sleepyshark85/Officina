using Sleepyshark.Officina;
using Sleepyshark.Officina.Claude;

namespace BookshopAssistant;

/// <summary>
/// The chat agent (ARCHITECTURE §12.1): frozen instructions, the bookshop tools, memory (APP-11) and, when given, the
/// export tools of the filesystem MCP server (APP-12). Who and when come as run context; what is remembered, the model
/// reads through the memory tool.
/// </summary>
public static class BookshopAgent
{
    /// <summary>The agent; in <c>demo</c> mode (APP-17), compaction and clearing come early enough to see in a short session.</summary>
    public static AgentDefinition Create(
        IModel model, BookshopTools tools, IMemoryStore memory, IApprover approver, IAuditSink audit, IEnumerable<string> secrets,
        TimeProvider time, IEnumerable<Tool>? exportTools = null, bool demo = false) => new()
        {
            Name = "bookshop",
            Model = model,
            Instructions = Instructions,
            Tools = [.. tools.All, MemoryTool.Create(memory), .. exportTools ?? []],
            Approver = approver,
            AuditSink = audit,
            Time = time,
            Secrets = [.. secrets],
            ContextManagement = demo ? Demo : LongConversations,
        };

    /// <summary>
    /// How long sessions stay short (HIST-01, HIST-02): compaction at Claude's default threshold, and clearing of old tool
    /// results only when it frees at least about two broad searches' worth, as each clearing rewrites the cached tail.
    /// </summary>
    public static readonly ContextManagement LongConversations = new()
    {
        CompactAt = 150_000,
        ClearToolResults = new ToolResultClearing(After: 20, Keep: 5, AtLeastTokens: 20_000),
    };

    /// <summary>
    /// Demo mode (APP-17): compaction at Claude's minimum, which the demo script's four catalogue searches of 10–15k tokens
    /// reach, and clearing when a request holds more than 12 tool calls (the API clears above <c>After</c>, not at it).
    /// Clearing comes first and counts every tool call, the memory tool's too, which the model calls once or twice a
    /// turn: at 4, it cleared the searches before they could compact. It keeps the 10 most recent results, so a turn of
    /// 8 parallel lookups plus a memory call or two never loses what it just fetched (at 2, the model fetched them
    /// again; at 8, a memory note pushed one out).
    /// </summary>
    public static readonly ContextManagement Demo = new()
    {
        CompactAt = 50_000,
        ClearToolResults = new ToolResultClearing(After: 12, Keep: 10),
    };

    /// <summary>The chat agent's model, which the application and the live smoke test (TEST-04) share.</summary>
    public static ClaudeModel Model(bool demo) => new()
    {
        Model = "claude-opus-5-5",
        Effort = ClaudeEffort.Medium,
        MaxOutputTokens = 16_000,

        // A demo is one sitting, and its large searches would cost 60% more to cache for an hour.
        CacheLifetime = demo ? CacheLifetime.FiveMinutes : CacheLifetime.OneHour,
    };

    /// <summary>The run context (APP-13, CTX-02): today's date and who is at the counter.</summary>
    public static string Context(DateTimeOffset now, string staffMember) =>
        $"Today is {now:dddd d MMMM yyyy}. The staff member using the assistant is {staffMember}.";

    private const string Instructions = """
        You are Bookshop Assistant, working alongside the staff of a small independent bookshop. Staff ask you, in
        plain language, about the catalogue, the stock, customers and their orders, and ask you to make changes for
        them. A message from the operator tells you today's date and which staff member you are talking to.

        How to work:
        - Use the tools for every fact about books, stock, customers and orders. Never guess an id, a price or a stock
          level; look it up. When a name could match several customers, ask which one is meant.
        - Requests often take several steps. Do the lookups first (they may run together), then the change. Before a
          change, say in one short sentence what you are about to do.
        - Changes (adding a customer, placing or cancelling an order, restocking) need the staff member's approval,
          which they give in their own interface. If they decline, accept it and offer an alternative.
        - When a tool returns an error, read it. Business rule failures, such as too few copies in stock, mean nothing
          changed: explain and offer a way forward, such as fewer copies or another book. If the database cannot be
          reached, say so plainly and suggest trying again shortly; do not pretend the change was made.
        - Prices are in pounds sterling. Give totals to the penny.
        - Asked to export a report, such as a customer's order history, look up the data, then write it as a CSV file
          with a header row into the exports folder, /projects/exports, named for its content (for example
          order-history-alice-martin.csv). Writing a file needs the staff member's approval. Tell them the file's name.
        - Your memory belongs to the staff member you are talking to. Keep their preferences and standing notes there,
          such as how they like prices shown, and follow them.

        How to answer:
        - Be brief and concrete: a few sentences, or a short list when there are several items. Name books by title and
          id, customers by name and id, orders by id.
        - Do not show raw JSON or tool names to the staff member.
        """;
}
