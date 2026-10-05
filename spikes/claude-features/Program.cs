// S02 spike: live check of the Claude features S00b did not prove, on the beta types only, streaming, Opus 5.5.
// Throwaway code, not part of the solution. Run: dotnet run -- [all|compact|clear|memory|updates|midsys|structured]
//
// The conversation is held as raw JSON messages. Assistant content is stored as each block's `.Json` text and the
// request is rebuilt from it (MessageCreateParams.FromRawUnchecked). A tapping HTTP handler captures the request body as
// sent, and every call checks that each stored assistant block appears in it byte for byte (feature 7).
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;

const string ModelId = "claude-opus-5-5";
var which = args.Length > 0 ? args[0] : "all";
var tap = new TapHandler { InnerHandler = new HttpClientHandler() };
AnthropicClient client = new() { HttpClient = new HttpClient(tap) { Timeout = TimeSpan.FromMinutes(10) } };
var ledger = new Ledger();
var roundTrip = new RoundTripStats();

async Task Run(string name, Func<Task> f)
{
    if (which != "all" && which != name) return;
    Console.WriteLine($"\n===== {name} =====");
    try { await f(); }
    catch (AnthropicApiException e) { Console.WriteLine($"!! {name} failed: {e.GetType().Name} {(int)e.StatusCode}: {e.ResponseBody}"); }
    catch (Exception e) { Console.WriteLine($"!! {name} failed: {e.GetType().FullName}: {e.Message}"); }
}

// ---------- helpers

static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);
static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
static JsonElement User(string text) => Parse(JsonSerializer.Serialize(new { role = "user", content = text }));
static JsonElement Sys(string text) => Parse(JsonSerializer.Serialize(new { role = "system", content = text }));
static JsonElement ToolResults(IEnumerable<(string id, string text)> results) =>
    Parse(JsonSerializer.Serialize(new
    {
        role = "user",
        content = results.Select(r => new { type = "tool_result", tool_use_id = r.id, content = r.text }).ToArray(),
    }));
// The assistant message is built from each block's raw JSON text, unchanged.
static JsonElement Assistant(BetaMessage m) =>
    Parse("{\"role\":\"assistant\",\"content\":[" + string.Join(",", m.Content.Select(b => b.Json.GetRawText())) + "]}");

static string Short(string s, int n = 160) => s.Length <= n ? s.Replace("\n", "\\n") : s[..n].Replace("\n", "\\n") + $"…(+{s.Length - n})";

static string BlockSummary(BetaContentBlock b)
{
    var raw = b.Json;
    var type = raw.GetProperty("type").GetString();
    return type switch
    {
        "thinking" => $"thinking(text={raw.GetProperty("thinking").GetString()!.Length}ch, sig={raw.GetProperty("signature").GetString()!.Length}ch)",
        "text" => $"text(\"{Short(raw.GetProperty("text").GetString()!, 80)}\")",
        "tool_use" => $"tool_use({raw.GetProperty("name").GetString()} {raw.GetProperty("input").GetRawText()})",
        "compaction" => $"compaction(content={(raw.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()!.Length + "ch" : "null")})",
        _ => type!,
    };
}

static async IAsyncEnumerable<T> Replay<T>(List<T> items)
{
    foreach (var i in items) yield return i;
    await Task.CompletedTask;
}

// One streamed call. `typed` carries everything but the messages; the messages are raw JSON.
async Task<BetaMessage> Call(string label, MessageCreateParams typed, List<JsonElement> msgs, Action<BetaRawMessageStreamEvent>? onEvent = null)
{
    var messagesJson = Parse("[" + string.Join(",", msgs.Select(m => m.GetRawText())) + "]");
    var body = new Dictionary<string, JsonElement>(typed.RawBodyData) { ["messages"] = messagesJson, ["stream"] = J(true) };
    var p = MessageCreateParams.FromRawUnchecked(typed.RawHeaderData, typed.RawQueryData, body);

    List<BetaRawMessageStreamEvent> events = [];
    for (int attempt = 1; ; attempt++)
    {
        events.Clear();
        try
        {
            await foreach (var ev in client.Beta.Messages.CreateStreaming(p))
            {
                events.Add(ev);
                onEvent?.Invoke(ev);
            }
            break;
        }
        catch (AnthropicSseException e) when (attempt < 4)
        {
            Console.WriteLine($"  [{label}] attempt {attempt}: mid-stream SSE error after {events.Count} events ({e.ErrorType}); retrying in 5s");
            await Task.Delay(5000);
        }
    }
    BetaMessage m = await Replay(events).Aggregate();

    // Feature 7: each stored assistant block must appear byte for byte in the body that went over the wire.
    int checkedBlocks = 0, mismatched = 0;
    foreach (var msg in msgs.Where(x => x.GetProperty("role").GetString() == "assistant"))
        foreach (var block in msg.GetProperty("content").EnumerateArray())
        {
            checkedBlocks++;
            if (!tap.LastRequestBody.Contains(block.GetRawText(), StringComparison.Ordinal))
            {
                mismatched++;
                Console.WriteLine($"  !! block not byte-identical on the wire: {Short(block.GetRawText(), 200)}");
            }
        }
    roundTrip.Add(checkedBlocks, mismatched);

    var usage = Parse(JsonSerializer.Serialize(m.Usage.RawData));
    var cost = ledger.Add(usage);
    Console.WriteLine($"  [{label}] stop={m.StopReason?.Raw()} blocks=[{string.Join(", ", m.Content.Select(BlockSummary))}]");
    Console.WriteLine($"  [{label}] usage in={m.Usage.InputTokens} out={m.Usage.OutputTokens} cache_read={m.Usage.CacheReadInputTokens} " +
        $"cache_write={m.Usage.CacheCreationInputTokens} | call ${cost:F4} running ${ledger.Total:F4} | replayed blocks checked={checkedBlocks} mismatched={mismatched}");
    if (usage.TryGetProperty("iterations", out var it) && it.ValueKind == JsonValueKind.Array)
        Console.WriteLine($"  [{label}] usage.iterations={it.GetRawText()}");
    if (m.RawData.TryGetValue("context_management", out var cm) && cm.ValueKind != JsonValueKind.Null)
        Console.WriteLine($"  [{label}] context_management={cm.GetRawText()}");
    return m;
}

// Instructions long enough to pass the 512-token cache minimum (frozen; never changes per run).
static string Instructions() =>
    "You are the assistant of a small bookshop called «Café Libro». Answer briefly and precisely.\n" +
    string.Join("\n", Enumerable.Range(1, 60).Select(i =>
        $"Policy {i}: when a customer asks about topic {i}, check the catalog first, never invent stock levels, and quote prices in euros."));

// ---------- 1: server-side compaction (compact_20260112) at a low trigger
await Run("compact", async () =>
{
    // A catalog big enough to pass the 50,000-token minimum trigger.
    var genres = new[] { "mystery", "history", "poetry", "science", "travel", "cooking" };
    string Catalog(int n) => string.Join("\n", Enumerable.Range(1, n).Select(i =>
        $"Item {i:D4}: \"Volume {i} of the {genres[i % 6]} series\" by Author {i * 7 % 997}, shelf {(char)('A' + i % 26)}{i % 40}, price {5 + i % 30}.{i % 100:D2} EUR."));
    var sys = new List<BetaTextBlockParam> { new() { Text = Instructions(), CacheControl = new BetaCacheControlEphemeral() } };
    var count = await client.Beta.Messages.CountTokens(new MessageCountTokensParams
    {
        Model = ModelId,
        System = sys,
        Messages = [new() { Role = Role.User, Content = Catalog(2000) }],
    });
    int lines = (int)(2000 * 52_000.0 / count.InputTokens);
    Console.WriteLine($"count_tokens: 2000 lines = {count.InputTokens} tokens -> using {lines} lines (~52k)");

    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 8000,
        Betas = ["compact-2026-01-12"],
        System = sys,
        CacheControl = new BetaCacheControlEphemeral(),
        OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
        ContextManagement = new BetaContextManagementConfig
        {
            Edits = [new BetaCompact20260112Edit { Trigger = new BetaInputTokensTrigger { ValueValue = 50_000 } }],
        },
        Messages = [],
    };
    Console.WriteLine("context_management json: " + JsonSerializer.Serialize(p.RawBodyData["context_management"]));

    List<JsonElement> msgs = [User(Catalog(lines) + "\n\nWhat is the title of item 0042? Reply with the title only.")];
    int compactionDeltas = 0;
    var m1 = await Call("compact#1", p, msgs, ev =>
    {
        if (ev.TryPickContentBlockDelta(out var d) && d.Delta.Json.GetProperty("type").GetString() == "compaction_delta") compactionDeltas++;
        if (ev.TryPickContentBlockStart(out var s) && s.ContentBlock.Json.GetProperty("type").GetString() == "compaction")
            Console.WriteLine("  stream: content_block_start compaction " + Short(s.ContentBlock.Json.GetRawText(), 120));
    });
    Console.WriteLine($"  stream: compaction_delta events={compactionDeltas}");
    foreach (var b in m1.Content.Where(b => b.Json.GetProperty("type").GetString() == "compaction"))
        Console.WriteLine("  compaction block (first 400 chars): " + Short(b.Json.GetRawText(), 400));

    msgs.Add(Assistant(m1));  // appended as received; the compaction block replaces everything before it
    msgs.Add(User("Which shelf is item 0042 on? Answer briefly."));
    var m2 = await Call("compact#2", p, msgs);
    msgs.Add(Assistant(m2));
    msgs.Add(User("And its price? Answer briefly."));
    await Call("compact#3", p, msgs);
});

// ---------- 2: tool-result clearing (clear_tool_uses_20250919) at a low threshold
await Run("clear", async () =>
{
    string Lookup(int id) => $"Book {id}: \"The Quiet Harbour, part {id}\" by Mara Linde.\n" + string.Join("\n", Enumerable.Range(1, 70).Select(i =>
        $"Review {i} of book {id}: readers praised chapter {i} for its pacing, its setting and the careful translation."));
    var schema = new Dictionary<string, JsonElement>
    {
        ["type"] = J("object"),
        ["properties"] = J(new { id = new { type = "integer", description = "Book id" } }),
        ["required"] = J(new[] { "id" }),
    };
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 4000,
        Betas = ["context-management-2025-06-27"],
        System = new List<BetaTextBlockParam> { new() { Text = Instructions(), CacheControl = new BetaCacheControlEphemeral() } },
        CacheControl = new BetaCacheControlEphemeral(),
        OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
        Tools = [new BetaTool { Name = "lookup_book", Description = "Look up one book by id. Returns its record and reviews.", InputSchema = new(schema) }],
        ToolChoice = new BetaToolChoiceAuto { DisableParallelToolUse = true },
        ContextManagement = new BetaContextManagementConfig
        {
            Edits = [new BetaClearToolUses20250919Edit
            {
                Trigger = new BetaToolUsesTrigger { Value = 2 },
                Keep = new BetaToolUsesKeep { Value = 1 },
            }],
        },
        Messages = [],
    };
    Console.WriteLine("context_management json: " + JsonSerializer.Serialize(p.RawBodyData["context_management"]));
    List<JsonElement> msgs = [User("Look up books 1, 2, 3 and 4 with lookup_book, one call per reply, in that order. Then tell me the author.")];
    for (int i = 1; i <= 6; i++)
    {
        var m = await Call($"clear#{i}", p, msgs);
        msgs.Add(Assistant(m));
        if (m.StopReason?.Raw() != "tool_use") break;
        var uses = m.Content.Where(b => b.Json.GetProperty("type").GetString() == "tool_use").Select(b => b.Json).ToList();
        msgs.Add(ToolResults(uses.Select(u => (u.GetProperty("id").GetString()!, Lookup(u.GetProperty("input").GetProperty("id").GetInt32())))));
    }
});

// ---------- 3: memory tool (memory_20250818) on the beta path, answered client-side
await Run("memory", async () =>
{
    var store = new Dictionary<string, string>();
    string Handle(JsonElement input)
    {
        string cmd = input.GetProperty("command").GetString()!;
        string S(string k) => input.TryGetProperty(k, out var v) ? v.ToString() : "";
        string path = S("path");
        switch (cmd)
        {
            case "view":
                if (store.TryGetValue(path, out var file))
                    return string.Join("\n", file.Split('\n').Select((l, i) => $"{i + 1,6}\t{l}"));
                var under = store.Keys.Where(k => k.StartsWith(path.TrimEnd('/') + "/")).ToList();
                return $"Here are the files and directories up to 2 levels deep in {path}:\n" +
                       string.Join("\n", under.Select(k => $"{Encoding.UTF8.GetByteCount(store[k])}\t{k}"));
            case "create": store[path] = S("file_text"); return $"File created successfully at: {path}";
            case "str_replace":
                if (!store.TryGetValue(path, out var f1)) return $"Error: The path {path} does not exist.";
                store[path] = f1.Replace(S("old_str"), S("new_str")); return "The memory file has been edited.";
            case "insert":
                if (!store.TryGetValue(path, out var f2)) return $"Error: The path {path} does not exist.";
                var ls = f2.Split('\n').ToList(); ls.Insert(int.Parse(S("insert_line")), S("insert_text"));
                store[path] = string.Join("\n", ls); return $"The file {path} has been edited.";
            case "delete": store.Remove(path); return $"Successfully deleted {path}";
            case "rename": store[S("new_path")] = store[S("old_path")]; store.Remove(S("old_path")); return "Renamed.";
            default: return $"Error: unknown command {cmd}";
        }
    }
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 4000,
        System = new List<BetaTextBlockParam> { new() { Text = Instructions(), CacheControl = new BetaCacheControlEphemeral() } },
        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
        Tools = [new BetaMemoryTool20250818()],
        Messages = [],
    };
    Console.WriteLine("tools json: " + JsonSerializer.Serialize(p.RawBodyData["tools"]));
    async Task Conversation(string label, string prompt)
    {
        List<JsonElement> msgs = [User(prompt)];
        for (int i = 1; i <= 6; i++)
        {
            var m = await Call($"{label}#{i}", p, msgs);
            msgs.Add(Assistant(m));
            if (m.StopReason?.Raw() != "tool_use") break;
            List<(string, string)> results = [];
            foreach (var u in m.Content.Where(b => b.Json.GetProperty("type").GetString() == "tool_use").Select(b => b.Json))
            {
                var r = Handle(u.GetProperty("input"));
                Console.WriteLine($"    memory {u.GetProperty("input").GetRawText()} -> {Short(r, 120)}");
                results.Add((u.GetProperty("id").GetString()!, r));
            }
            msgs.Add(ToolResults(results));
        }
    }
    await Conversation("memA", "Hi, I'm Ana. For future conversations, please remember that I love mystery novels and dislike horror.");
    Console.WriteLine("  store after A: " + JsonSerializer.Serialize(store));
    await Conversation("memB", "Hello again. Which genre should I browse today? One sentence.");
});

// ---------- 4: thinking display "updates" with a tool loop, streamed and replayed
await Run("updates", async () =>
{
    var titleSchema = new Dictionary<string, JsonElement>
    {
        ["type"] = J("object"),
        ["properties"] = J(new { title = new { type = "string" } }),
        ["required"] = J(new[] { "title" }),
    };
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 4000,
        Betas = ["thinking-display-updates-2026-08-18"],
        System = new List<BetaTextBlockParam> { new() { Text = Instructions(), CacheControl = new BetaCacheControlEphemeral() } },
        CacheControl = new BetaCacheControlEphemeral(),
        Thinking = new BetaThinkingConfigAdaptive { Display = Display.Updates },
        OutputConfig = new BetaOutputConfig { Effort = Environment.GetEnvironmentVariable("UPDATES_PLAIN") is null ? Effort.Medium : Effort.High },
        Tools =
        [
            new BetaTool { Name = "check_stock", Description = "Number of copies in stock for a title.", InputSchema = new(titleSchema) },
            new BetaTool { Name = "get_price", Description = "Price in euros for a title.", InputSchema = new(titleSchema) },
        ],
        Messages = [],
    };
    Console.WriteLine("thinking json: " + JsonSerializer.Serialize(p.RawBodyData["thinking"]));
    List<JsonElement> msgs = [User("For \"The Name of the Rose\", \"Gaudy Night\" and \"The Daughter of Time\": check stock and price of each, one title at a time, " +
        (Environment.GetEnvironmentVariable("UPDATES_PLAIN") is null ? "and keep me posted on what you are doing between the lookups. " : "") +
        "Then tell me which in-stock title is cheapest.")];
    for (int i = 1; i <= 8; i++)
    {
        var deltas = new StringBuilder();
        int thinkingDeltas = 0, sigDeltas = 0;
        var m = await Call($"updates#{i}", p, msgs, ev =>
        {
            if (!ev.TryPickContentBlockDelta(out var d)) return;
            if (d.Delta.TryPickThinking(out var t)) { thinkingDeltas++; deltas.Append('[').Append(t.Thinking).Append(']'); }
            else if (d.Delta.TryPickSignature(out _)) sigDeltas++;
        });
        Console.WriteLine($"    stream: thinking_delta={thinkingDeltas} signature_delta={sigDeltas} deltas={Short(deltas.ToString(), 300)}");
        foreach (var b in m.Content.Where(b => b.Json.GetProperty("type").GetString() == "thinking"))
            Console.WriteLine($"    thinking block: {Short(b.Json.GetRawText(), 260)}");
        msgs.Add(Assistant(m));
        if (m.StopReason?.Raw() != "tool_use") break;
        msgs.Add(ToolResults(m.Content.Where(b => b.Json.GetProperty("type").GetString() == "tool_use").Select(b => b.Json)
            .Select(u => (u.GetProperty("id").GetString()!, u.GetProperty("name").GetString() == "check_stock"
                ? $"{u.GetProperty("input").GetProperty("title").GetString()!.Length % 4} copies"
                : $"{10 + u.GetProperty("input").GetProperty("title").GetString()!.Length % 7}.90 EUR"))));
    }
});

// ---------- 5: mid-conversation system message carrying run context, cache point on last system block + automatic caching
await Run("midsys", async () =>
{
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 2000,
        System = new List<BetaTextBlockParam> { new() { Text = Instructions(), CacheControl = new BetaCacheControlEphemeral() } },
        CacheControl = new BetaCacheControlEphemeral(),
        OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
        Messages = [],
    };
    string Ctx() => "Run context: today is 2026-10-05; the customer is Ana Muñoz (loyalty member).";
    List<JsonElement> msgs = [User("Hi! Greet me by name and tell me today's date."), Sys(Ctx())];
    var m1 = await Call("midsys#1", p, msgs);
    Console.WriteLine("    request messages: " + Short(tap.LastRequestBody[tap.LastRequestBody.IndexOf("\"messages\"")..], 300));
    Console.WriteLine("    stored text block raw: " + Short(m1.Content.Last().Json.GetRawText(), 200));
    msgs.Add(Assistant(m1));
    msgs.Add(User("Recommend one mystery novel, in one sentence."));
    msgs.Add(Sys(Ctx()));
    var m2 = await Call("midsys#2", p, msgs);
    msgs.Add(Assistant(m2));
    msgs.Add(User("And one more, also one sentence."));
    msgs.Add(Sys(Ctx()));
    await Call("midsys#3", p, msgs);
});

// ---------- 6: structured output with a schema from .NET 10 JsonSchemaExporter
await Run("structured", async () =>
{
    var opts = new JsonSerializerOptions(JsonSerializerOptions.Web) { Converters = { new JsonStringEnumConverter() } };
    var exported = opts.GetJsonSchemaAsNode(typeof(BookPick), new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
    Console.WriteLine("exported schema: " + exported.ToJsonString());

    async Task<bool> Try(string label, JsonNode schema, string prompt = "Pick one mystery novel for Ana.")
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.ToJsonString())!;
        MessageCreateParams p = new()
        {
            Model = ModelId,
            MaxTokens = 2000,
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low, Format = new BetaJsonOutputFormat { Schema = dict } },
            Messages = [],
        };
        try
        {
            var m = await Call(label, p, [User(prompt)]);
            var text = string.Concat(m.Content.Where(b => b.Json.GetProperty("type").GetString() == "text").Select(b => b.Json.GetProperty("text").GetString()));
            Console.WriteLine($"    {label} accepted; output: {Short(text, 300)}");
            if (label.StartsWith("exported"))
            {
                try { JsonSerializer.Deserialize<BookPick>(text, opts); Console.WriteLine($"    {label} deserializes into BookPick: yes"); }
                catch (Exception e) { Console.WriteLine($"    {label} deserializes into BookPick: NO ({e.Message})"); }
            }
            return true;
        }
        catch (AnthropicBadRequestException e)
        {
            Console.WriteLine($"    {label} REJECTED 400: {Short(e.ResponseBody ?? e.Message, 400)}");
            return false;
        }
    }

    // A: as exported. B: with additionalProperties:false added to every object.
    await Try("exported-as-is", exported.DeepClone());
    var closed = exported.DeepClone();
    void Close(JsonNode? n)
    {
        if (n is JsonObject o)
        {
            if (o["type"] is JsonNode t && t.ToJsonString().Contains("\"object\"") && o["additionalProperties"] is null) o["additionalProperties"] = false;
            foreach (var kv in o.ToList()) Close(kv.Value);
        }
        else if (n is JsonArray a) foreach (var x in a) Close(x);
    }
    Close(closed);
    await Try("exported-closed", closed);

    // Keyword probes on a one-property schema; a 400 costs nothing.
    var probes = new (string name, string prop)[]
    {
        ("type-array-null", "{\"type\":[\"string\",\"null\"]}"),
        ("enum", "{\"enum\":[\"Mystery\",\"Horror\"]}"),
        ("pattern", "{\"type\":\"string\",\"pattern\":\"^[A-Z].*$\"}"),
        ("number-or-string", "{\"type\":[\"string\",\"number\"],\"pattern\":\"^-?(?:0|[1-9]\\\\d*)(?:\\\\.\\\\d+)?(?:[eE][+-]?\\\\d+)?$\"}"),
        ("minimum-maximum", "{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}"),
        ("minLength", "{\"type\":\"string\",\"minLength\":3,\"maxLength\":20}"),
        ("format-date", "{\"type\":\"string\",\"format\":\"date\"}"),
        ("minItems-maxItems", "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1,\"maxItems\":3}"),
        ("default", "{\"type\":\"string\",\"default\":\"x\"}"),
        ("minItems-only", "{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1}"),
        ("number-minimum", "{\"type\":\"number\",\"minimum\":1}"),
        ("pattern-enforced", "{\"type\":\"string\",\"pattern\":\"^[0-9]{3}$\"}"),
        ("anyOf-null", "{\"anyOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}"),
        ("ref-defs", "{\"$ref\":\"#/$defs/n\"}"),
    };
    foreach (var (name, prop) in probes)
    {
        var s = JsonNode.Parse($"{{\"type\":\"object\",\"properties\":{{\"x\":{prop}}},\"required\":[\"x\"],\"additionalProperties\":false}}")!;
        if (name == "ref-defs") s["$defs"] = JsonNode.Parse("{\"n\":{\"type\":\"object\",\"properties\":{\"v\":{\"type\":\"string\"}},\"required\":[\"v\"],\"additionalProperties\":false}}");
        var prompt = name switch { "minimum-maximum" => "Set x to 250.", "pattern-enforced" => "Set x to the word hello.", _ => "Fill x with a sensible value." };
        await Try($"probe-{name}", s, prompt);
    }
    var withSchemaKw = JsonNode.Parse("{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"string\"}},\"required\":[\"x\"],\"additionalProperties\":false}")!;
    await Try("probe-$schema", withSchemaKw);
});

Console.WriteLine($"\nROUND TRIP: replayed assistant blocks checked={roundTrip.Checked} mismatched={roundTrip.Mismatched}");
Console.WriteLine($"TOTAL: {ledger}");

// ---------- types

enum Genre { Mystery, Horror, History }

record Note(string Text);

record BookPick(string Title, string Author, int Year, decimal Price, Genre Genre, List<string> Tags, string? Isbn, Note? Note);

sealed class TapHandler : DelegatingHandler
{
    public string LastRequestBody { get; private set; } = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is not null) LastRequestBody = await request.Content.ReadAsStringAsync(ct);
        return await base.SendAsync(request, ct);
    }
}

sealed class RoundTripStats
{
    public int Checked, Mismatched;
    public void Add(int c, int m) { Checked += c; Mismatched += m; }
}

sealed class Ledger
{
    long input, output, read, w5, w1;
    public double Total { get; private set; }
    static long L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
    // Opus 5.5: $4 input, $20 output, cache read $0.20, 5m write 1.25x, 1h write 2x (per MTok).
    // When usage.iterations is present, the top-level counts cover only the message iteration, so sum the iterations.
    public double Add(JsonElement usage)
    {
        IEnumerable<JsonElement> parts = usage.TryGetProperty("iterations", out var it) && it.ValueKind == JsonValueKind.Array && it.GetArrayLength() > 0
            ? it.EnumerateArray().ToList() : [usage];
        double cost = 0;
        foreach (var u in parts)
        {
            long i = L(u, "input_tokens"), o = L(u, "output_tokens"), r = L(u, "cache_read_input_tokens");
            long a = 0, b = 0;
            if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
            { a = L(cc, "ephemeral_5m_input_tokens"); b = L(cc, "ephemeral_1h_input_tokens"); }
            else a = L(u, "cache_creation_input_tokens");
            input += i; output += o; read += r; w5 += a; w1 += b;
            cost += (i * 4 + o * 20 + r * 0.20 + a * 5 + b * 8) / 1e6;
        }
        Total += cost;
        return cost;
    }
    public override string ToString() => $"input={input} output={output} cache_read={read} write5m={w5} write1h={w1} cost=${Total:F4}";
}
