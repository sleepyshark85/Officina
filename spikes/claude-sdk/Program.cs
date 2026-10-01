// S00b spike: exercise the official Anthropic C# SDK against every feature the Claude provider needs.
// Throwaway code. Run: dotnet run -- [all|stream|cache|midsys|clearat|structured|raw|error]
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Beta = Anthropic.Models.Beta.Messages;

const string ModelId = "claude-opus-5-5";
var which = args.Length > 0 ? args[0] : "all";
AnthropicClient client = new(); // reads ANTHROPIC_API_KEY; never printed
var totals = new Totals();

async Task Run(string name, Func<Task> f)
{
    if (which != "all" && which != name) return;
    Console.WriteLine($"\n===== {name} =====");
    try { await f(); }
    catch (Exception e) { Console.WriteLine($"!! {name} failed: {e.GetType().FullName}: {e.Message}"); }
}

void PrintUsage(string label, Usage u)
{
    totals.Add(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0,
        u.CacheCreation?.Ephemeral5mInputTokens ?? 0, u.CacheCreation?.Ephemeral1hInputTokens ?? 0);
    Console.WriteLine($"[usage {label}] input={u.InputTokens} output={u.OutputTokens} " +
        $"cache_read={u.CacheReadInputTokens} cache_creation={u.CacheCreationInputTokens} " +
        $"(5m={u.CacheCreation?.Ephemeral5mInputTokens} 1h={u.CacheCreation?.Ephemeral1hInputTokens}) " +
        $"server_tool_use={(u.ServerToolUse is null ? "null" : "set")} service_tier={u.ServiceTier?.Raw()}");
}

void PrintBetaUsage(string label, Beta.BetaUsage u)
{
    totals.Add(u.InputTokens, u.OutputTokens, u.CacheReadInputTokens ?? 0,
        u.CacheCreation?.Ephemeral5mInputTokens ?? 0, u.CacheCreation?.Ephemeral1hInputTokens ?? 0);
    Console.WriteLine($"[usage {label}] input={u.InputTokens} output={u.OutputTokens} " +
        $"cache_read={u.CacheReadInputTokens} cache_creation={u.CacheCreationInputTokens}");
}

// Convert response content to request params, preserving thinking blocks byte-for-byte (signature included).
List<ContentBlockParam> ToParams(IReadOnlyList<ContentBlock> content)
{
    List<ContentBlockParam> list = [];
    foreach (var b in content)
    {
        if (b.TryPickText(out var t)) list.Add(new TextBlockParam { Text = t.Text });
        else if (b.TryPickThinking(out var th)) list.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
        else if (b.TryPickRedactedThinking(out var r)) list.Add(new RedactedThinkingBlockParam { Data = r.Data });
        else if (b.TryPickToolUse(out var tu)) list.Add(new ToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
        else throw new NotSupportedException("unhandled block " + b.Json);
    }
    return list;
}

static async IAsyncEnumerable<T> Replay<T>(List<T> items)
{
    foreach (var i in items) yield return i;
    await Task.CompletedTask;
}

static JsonElement J(object o) => JsonSerializer.SerializeToElement(o);

// ---------- 0: isolate streaming failures (plain stream, then with tool but no thinking display)
await Run("stream0", async () =>
{
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 200,
        OutputConfig = new OutputConfig { Effort = Effort.Low },
        Messages = [new() { Role = Role.User, Content = "Reply with just OK." }],
    };
    int n = 0;
    try { await foreach (var ev in client.Messages.CreateStreaming(p)) n++; Console.WriteLine($"plain stream ok, events={n}"); }
    catch (AnthropicSseException e) { Console.WriteLine($"plain stream SSE error after {n} events: ErrorType={e.ErrorType}"); }
    var m = await client.Messages.Create(p);
    Console.WriteLine($"non-streaming ok: {m.StopReason.Raw()}");
});

// ---------- 1 + 5 + 6(strict): streaming with an eager, strict client tool; adaptive thinking + effort; thinking round-trip
await Run("stream", async () =>
{
    // InputSchema has typed Type/Properties/Required only; additionalProperties:false (needed by strict) goes via raw data.
    var schema = new InputSchema(new Dictionary<string, JsonElement>
    {
        ["type"] = J("object"),
        ["properties"] = J(new { city = new { type = "string" } }),
        ["required"] = J(new[] { "city" }),
        ["additionalProperties"] = J(false),
    });
    var tool = new Tool
    {
        Name = "get_weather",
        Description = "Get current weather for a city.",
        InputSchema = schema,
        Strict = true,               // typed
        EagerInputStreaming = true,  // typed
    };
    List<MessageParam> messages = [new() { Role = Role.User, Content = "Which city is the capital of the country whose name is an anagram of NIAPS? Work it out carefully, then use the tool to get its weather." }];
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 2000,
        Thinking = Environment.GetEnvironmentVariable("NO_DISPLAY") is null ? new ThinkingConfigAdaptive { Display = Display.Summarized } : null,
        OutputConfig = new OutputConfig { Effort = Effort.High },
        Tools = [tool],
        ToolChoice = new ToolChoiceAuto(),
        Messages = messages,
    };
    Console.WriteLine("request tool json: " + JsonSerializer.Serialize(p.RawBodyData["tools"]));

    List<RawMessageStreamEvent> events = [];
    int jsonDeltas = 0, thinkingDeltas = 0, sigDeltas = 0;
    var partial = new StringBuilder();
    for (int attempt = 1; ; attempt++)
    {
        events.Clear(); jsonDeltas = thinkingDeltas = sigDeltas = 0; partial.Clear();
        try
        {
            await foreach (var ev in client.Messages.CreateStreaming(p))
            {
                events.Add(ev);
                if (ev.TryPickContentBlockDelta(out var d))
                {
                    if (d.Delta.TryPickInputJson(out var ij)) { jsonDeltas++; partial.Append(ij.PartialJson); }
                    else if (d.Delta.TryPickThinking(out _)) thinkingDeltas++;
                    else if (d.Delta.TryPickSignature(out _)) sigDeltas++;
                }
            }
            break;
        }
        catch (AnthropicSseException e) when (attempt < 4)
        {
            Console.WriteLine($"attempt {attempt}: mid-stream SSE error after {events.Count} events, ErrorType={e.ErrorType}; retrying in 5s");
            await Task.Delay(5000);
        }
    }
    Console.WriteLine($"events={events.Count} input_json_deltas={jsonDeltas} thinking_deltas={thinkingDeltas} signature_deltas={sigDeltas}");
    Console.WriteLine($"accumulated partial_json: {partial}");
    // validate before running the tool (eager streaming = no server-side validation)
    using (var doc = JsonDocument.Parse(partial.ToString()))
        Console.WriteLine($"parsed city={doc.RootElement.GetProperty("city").GetString()}");

    Message msg = await Replay(events).Aggregate();  // SDK aggregator -> final Message
    Console.WriteLine($"stop_reason={msg.StopReason.Raw()} blocks=[{string.Join(",", msg.Content.Select(b => b.Value?.GetType().Name))}]");
    foreach (var b in msg.Content)
        if (b.TryPickThinking(out var th)) Console.WriteLine($"thinking len={th.Thinking.Length} sig len={th.Signature.Length}");
    PrintUsage("stream#1", msg.Usage);

    var toolUse = msg.Content.Select(b => b.Value).OfType<ToolUseBlock>().First();
    messages.Add(new() { Role = Role.Assistant, Content = ToParams(msg.Content) }); // thinking sent back unchanged
    messages.Add(new()
    {
        Role = Role.User,
        Content = new List<ContentBlockParam> { new ToolResultBlockParam { ToolUseID = toolUse.ID, Content = "18C, light rain" } },
    });
    var msg2 = await client.Messages.CreateStreaming(p with { Messages = messages }).Aggregate();
    Console.WriteLine($"follow-up stop_reason={msg2.StopReason.Raw()} text={string.Join("", msg2.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim()}");
    PrintUsage("stream#2", msg2.Usage);
});

// ---------- 2 + 9: cache_control 1h on last system block, 5m on last message block; second call reads
await Run("cache", async () =>
{
    var sys = string.Join(" ", Enumerable.Range(1, 160).Select(i => $"Rule {i}: always answer tersely and precisely about topic {i}."));
    var ctx = string.Join(" ", Enumerable.Range(1, 120).Select(i => $"Note {i}: the sample value for item {i} is {i * 7}."));
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 200,
        OutputConfig = new OutputConfig { Effort = Effort.Low },
        System = new List<TextBlockParam>
        {
            new() { Text = sys, CacheControl = new CacheControlEphemeral { Ttl = Ttl.Ttl1h } },
        },
        Messages =
        [
            new()
            {
                Role = Role.User,
                Content = new List<ContentBlockParam>
                {
                    new TextBlockParam { Text = ctx + " Reply with just OK.", CacheControl = new CacheControlEphemeral { Ttl = Ttl.Ttl5m } },
                },
            },
        ],
    };
    Console.WriteLine("system json: " + JsonSerializer.Serialize(p.RawBodyData["system"]).Substring(0, 60) + "... cache_control=" +
        JsonSerializer.Serialize(p.RawBodyData["system"][0].GetProperty("cache_control")));
    var m1 = await client.Messages.Create(p);
    PrintUsage("cache#1", m1.Usage);
    var m2 = await client.Messages.Create(p);
    PrintUsage("cache#2", m2.Usage);
});

// ---------- 3: mid-conversation role:system message (non-beta, typed Role.System)
await Run("midsys", async () =>
{
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 300,
        OutputConfig = new OutputConfig { Effort = Effort.Low },
        System = "You are a helpful assistant.",
        Messages =
        [
            new() { Role = Role.User, Content = "Say hello." },
            new() { Role = Role.System, Content = "Operator: from now on reply only in French." },
        ],
    };
    Console.WriteLine("messages json: " + JsonSerializer.Serialize(p.RawBodyData["messages"]));
    var m = await client.Messages.Create(p);
    Console.WriteLine("reply: " + string.Join("", m.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim());
    PrintUsage("midsys", m.Usage);
});

// ---------- 4: turn-scoped system message (clear_at next_user_message, beta) - typed on Beta.BetaMessageParam
await Run("clearat", async () =>
{
    List<Beta.BetaMessageParam> msgs =
    [
        new() { Role = Beta.Role.User, Content = "What is the code word? Answer with the word only." },
        new() { Role = Beta.Role.System, Content = "Volatile context: the code word is PELICAN.", ClearAt = Beta.ClearAt.NextUserMessage },
    ];
    Beta.MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 300,
        Betas = ["mid-conversation-system-clear-at-2026-08-21"],
        OutputConfig = new Beta.BetaOutputConfig { Effort = Beta.Effort.Low },
        Messages = msgs,
    };
    Console.WriteLine("messages json: " + JsonSerializer.Serialize(p.RawBodyData["messages"]));
    var m1 = await client.Beta.Messages.Create(p);
    var t1 = string.Join("", m1.Content.Select(b => b.Value).OfType<Beta.BetaTextBlock>().Select(t => t.Text)).Trim();
    Console.WriteLine("turn1 reply: " + t1);
    PrintBetaUsage("clearat#1", m1.Usage);
    // next turn: earlier system message kept unchanged in the array, but cleared for the model
    List<Beta.BetaContentBlockParam> echo = [];
    foreach (var b in m1.Content)
    {
        if (b.TryPickThinking(out var th)) echo.Add(new Beta.BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
        else if (b.TryPickText(out var tx)) echo.Add(new Beta.BetaTextBlockParam { Text = tx.Text });
    }
    msgs.Add(new() { Role = Beta.Role.Assistant, Content = echo });
    msgs.Add(new() { Role = Beta.Role.User, Content = "Is there any system message visible to you right now that mentions a code word? Answer yes or no, then quote it if yes." });
    var m2 = await client.Beta.Messages.Create(p with { Messages = msgs });
    Console.WriteLine("turn2 reply: " + string.Join("", m2.Content.Select(b => b.Value).OfType<Beta.BetaTextBlock>().Select(t => t.Text)).Trim());
    PrintBetaUsage("clearat#2", m2.Usage);
});

// ---------- 6: structured output (output_config.format)
await Run("structured", async () =>
{
    MessageCreateParams p = new()
    {
        Model = ModelId,
        MaxTokens = 500,
        OutputConfig = new OutputConfig
        {
            Effort = Effort.Low,
            Format = new JsonOutputFormat
            {
                Schema = new Dictionary<string, JsonElement>
                {
                    ["type"] = J("object"),
                    ["properties"] = J(new { capital = new { type = "string" }, population_millions = new { type = "number" } }),
                    ["required"] = J(new[] { "capital", "population_millions" }),
                    ["additionalProperties"] = J(false),
                },
            },
        },
        Messages = [new() { Role = Role.User, Content = "Capital of France and its population in millions." }],
    };
    var m = await client.Messages.Create(p);
    var json = string.Join("", m.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
    Console.WriteLine("structured: " + json);
    using var doc = JsonDocument.Parse(json);
    Console.WriteLine("parsed capital=" + doc.RootElement.GetProperty("capital").GetString());
    PrintUsage("structured", m.Usage);
});

// ---------- 7: raw body fields. (a) a field the non-beta params type has no property for: `fallbacks` + beta header,
// both injected through the raw-data constructor; (b) a nested raw field was already shown (additionalProperties).
await Run("raw", async () =>
{
    MessageCreateParams typed = new()
    {
        Model = ModelId,
        MaxTokens = 200,
        OutputConfig = new OutputConfig { Effort = Effort.Low },
        Messages = [new() { Role = Role.User, Content = "Reply with just OK." }],
    };
    var body = new Dictionary<string, JsonElement>(typed.RawBodyData) { ["fallbacks"] = J("default") };
    var headers = new Dictionary<string, JsonElement>(typed.RawHeaderData) { ["anthropic-beta"] = J("server-side-fallback-2026-07-01") };
    var raw = MessageCreateParams.FromRawUnchecked(headers, typed.RawQueryData, body);
    var resp = await client.WithRawResponse.Messages.Create(raw);
    Console.WriteLine($"raw call HTTP {(int)resp.StatusCode}; request-id header present={resp.Headers.Any(h => h.Key == "request-id")}");
    var m = await resp.Deserialize();
    Console.WriteLine("reply: " + string.Join("", m.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim());
    // undocumented response fields are reachable through the model's raw JSON
    Console.WriteLine("response top-level keys: " + string.Join(",", m.RawData.Keys));
    PrintUsage("raw", m.Usage);
});

// ---------- 8: errors - a raw unknown field proves the raw body reaches the wire and provokes a 400
await Run("error", async () =>
{
    MessageCreateParams typed = new()
    {
        Model = ModelId,
        MaxTokens = 50,
        Messages = [new() { Role = Role.User, Content = "hi" }],
    };
    var body = new Dictionary<string, JsonElement>(typed.RawBodyData) { ["not_a_real_param"] = J(1) };
    try
    {
        await client.Messages.Create(MessageCreateParams.FromRawUnchecked(typed.RawHeaderData, typed.RawQueryData, body));
        Console.WriteLine("unexpected success");
    }
    catch (AnthropicBadRequestException e)
    {
        Console.WriteLine($"caught {e.GetType().Name} : {e.GetType().BaseType!.Name} : {e.GetType().BaseType!.BaseType!.Name}");
        Console.WriteLine($"StatusCode={(int)e.StatusCode} ErrorType={e.ErrorType} ResponseBody={e.ResponseBody}");
    }
    // 404 via bad model
    try { await client.Messages.Create(typed with { Model = "claude-does-not-exist" }); }
    catch (AnthropicApiException e) { Console.WriteLine($"caught {e.GetType().Name} StatusCode={(int)e.StatusCode} ErrorType={e.ErrorType}"); }
    // streaming 400: is it thrown at the HTTP level (AnthropicBadRequestException) or as an SSE error?
    try { await foreach (var _ in client.Messages.CreateStreaming(MessageCreateParams.FromRawUnchecked(typed.RawHeaderData, typed.RawQueryData, body))) { } }
    catch (AnthropicServiceException e) { Console.WriteLine($"streaming: caught {e.GetType().Name} ErrorType={e.ErrorType}"); }
});

Console.WriteLine($"\nTOTAL tokens: {totals}");
Console.WriteLine($"Approx cost at $4/$20 per MTok, ${totals.Cost():F4}");

sealed class Totals
{
    long input, output, read, w5, w1;
    public void Add(long i, long o, long r, long a, long b) { input += i; output += o; read += r; w5 += a; w1 += b; }
    // Opus 5.5: $4 input, $20 output, cache read $0.20, 5m write 1.25x, 1h write 2x
    public double Cost() => (input * 4 + output * 20 + read * 0.20 + w5 * 5 + w1 * 8) / 1e6;
    public override string ToString() => $"input={input} output={output} cache_read={read} write5m={w5} write1h={w1}";
}
