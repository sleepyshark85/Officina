using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>Tools from typed functions (TOOL-01) and the schema subset (Q2).</summary>
public class ToolDefinitionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum Genre
    {
        Fantasy,
        Crime,
    }

    public sealed record Line(string Isbn, int Copies, [property: Description("A note for the packer.")] string? Note = null);

    [Fact]
    public void The_schema_is_derived_from_the_parameters()
    {
        var tool = Tool.FromFunction(
            "search", "Searches the catalogue.", ToolKind.Read,
            ([Description("Words in the title.")] string title, Genre genre, decimal? maxPrice, int limit = 10, CancellationToken cancellationToken = default) => "");

        Assert.Equal(
            """{"type":"object","properties":{"title":{"description":"Words in the title.","type":"string"},"genre":{"enum":["Fantasy","Crime"]},"maxPrice":{"type":["number","null"]},"limit":{"type":"integer"}},"required":["title","genre","maxPrice"],"additionalProperties":false}""",
            tool.InputSchema);
        Assert.Equal(ToolKind.Read, tool.Kind);
    }

    [Fact]
    public void A_record_parameter_becomes_a_closed_object_with_its_required_members()
    {
        var tool = Tool.FromFunction("order", "Places an order.", ToolKind.Write, (Line[] lines) => "", needsApproval: true);

        var line = JsonNode.Parse(tool.InputSchema)!["properties"]!["lines"]!["items"]!;
        Assert.Equal(
            """{"type":"object","properties":{"isbn":{"type":"string"},"copies":{"type":"integer"},"note":{"description":"A note for the packer.","type":["string","null"],"default":null}},"required":["isbn","copies"],"additionalProperties":false}""",
            line.ToJsonString());
        Assert.True(tool.NeedsApproval);
    }

    [Fact]
    public async Task A_typed_function_runs_with_its_arguments_and_its_result_goes_back_as_JSON()
    {
        var tool = Tool.FromFunction(
            "order", "Places an order.", ToolKind.Write,
            async (string customer, Line[] lines, int discount = 5, CancellationToken cancellationToken = default) =>
            {
                await Task.Yield();
                return new { customer, copies = lines.Sum(line => line.Copies), discount, cancellable = cancellationToken.CanBeCanceled };
            });
        var model = new ScriptedModel()
            .CallTools(new ToolCall("c1", "order", """{"customer":"Alice","lines":[{"isbn":"1","copies":2},{"isbn":"2","copies":1,"note":"gift"}]}"""))
            .Reply("Ordered.");
        var conversation = new Conversation();

        await Agents.With(model, tools: tool).RunAsync(conversation, "Order.", cancellationToken: Ct);

        Assert.Equal(new ToolResult("c1", """{"customer":"Alice","copies":3,"discount":5,"cancellable":true}""", false), conversation.Messages[2].Blocks[0].ToolResult);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("task")]
    [InlineData("value task")]
    [InlineData("output")]
    [InlineData("nothing")]
    public async Task Each_kind_of_return_value_becomes_a_result(string kind)
    {
        var tool = kind switch
        {
            "text" => Tool.FromFunction("t", "T.", ToolKind.Read, () => "plain"),
            "task" => Tool.FromFunction("t", "T.", ToolKind.Read, () => Task.FromResult(42)),
            "value task" => Tool.FromFunction("t", "T.", ToolKind.Read, () => ValueTask.FromResult("plain")),
            "output" => Tool.FromFunction("t", "T.", ToolKind.Read, () => new ToolOutput("Not enough stock.", IsError: true)),
            _ => Tool.FromFunction("t", "T.", ToolKind.Read, () => Task.CompletedTask),
        };

        var output = await tool.Handler(JsonDocument.Parse("{}").RootElement, new ToolContext(null), Ct);

        Assert.Equal(
            kind switch
            {
                "text" or "value task" => new ToolOutput("plain"),
                "task" => new ToolOutput("42"),
                "output" => new ToolOutput("Not enough stock.", true),
                _ => new ToolOutput(""),
            },
            output);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"a":{"oneOf":[{"type":"string"}]}}}""", "/properties/a/oneOf")]
    [InlineData("""{"type":"object","$ref":"#/$defs/a"}""", "/$ref")]
    [InlineData("""{"type":"object","properties":{"a":{"type":"date"}}}""", "/properties/a/type")]
    [InlineData("""{"type":"object","properties":{"a":{"minLength":-1}}}""", "/properties/a/minLength")]
    public void A_schema_outside_the_subset_is_refused_when_the_tool_is_defined(string schema, string at)
    {
        var refused = Assert.Throws<ArgumentException>(() => Agents.Tool("t", schema: schema));

        Assert.Contains($"'{at}'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_input_schema_must_be_an_object_schema()
    {
        Assert.Throws<ArgumentException>(() => Agents.Tool("t", schema: """{"type":"string"}"""));
        Assert.Throws<ArgumentException>(() => Agents.Tool("t", schema: "{}"));
    }
}
