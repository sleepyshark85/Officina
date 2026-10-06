using System.Text.Json;
using Sleepyshark.Officina.Testing;
using static Sleepyshark.Officina.Claude.Tests.StreamTests;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>MEM-01 on Claude: the memory tool is Claude's native one, in its place among the sorted tools, and its calls run.</summary>
public class MemoryRequestTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A reply that asks the memory tool to create a file, as Claude streams it.</summary>
    private static readonly string CreateCall = Sse.Events(
        Sse.Start,
        """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"toolu_m1","name":"memory","input":{}}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"create\",\"path\":\"/memories/prefs.md\","}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"\"file_text\":\"Prices with tax.\"}"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":5}}""",
        """{"type":"message_stop"}""");

    [Fact]
    public async Task The_memory_tool_is_sent_as_claude_s_native_tool_in_sorted_order_and_its_calls_run_in_the_run_s_scope()
    {
        var api = new FakeApi().Stream(CreateCall).Stream(Sse.Text());
        using var model = Model(api);
        var store = new InMemoryMemoryStore();
        var agent = new AgentDefinition
        {
            Model = model,
            Instructions = "Answer briefly.",
            Tools =
            [
                new Tool("search", "Searches.", """{"type":"object"}""", ToolKind.Read, (_, _) => Task.FromResult(new ToolOutput("ok"))),
                MemoryTool.Create(store),
                new Tool("add", "Adds.", """{"type":"object"}""", ToolKind.Write, (_, _) => Task.FromResult(new ToolOutput("ok"))),
            ],
        };

        var result = await agent.RunAsync(new Conversation(), "I prefer prices with tax.", new() { MemoryScope = "sam" }, Ct);

        Assert.IsType<Completed>(result);
        Assert.Equal("Prices with tax.", await store.ReadAsync("sam", "prefs.md", Ct));
        foreach (var sent in api.Requests)
        {
            var tools = JsonDocument.Parse(sent).RootElement.GetProperty("tools").EnumerateArray().ToList();
            Assert.Equal(["add", "memory", "search"], tools.Select(tool => tool.GetProperty("name").GetString()));
            Assert.Equal("""{"name":"memory","type":"memory_20250818"}""", tools[1].GetRawText());
        }
    }
}
