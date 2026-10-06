using System.ComponentModel;
using System.Text.Json;
using static Sleepyshark.Officina.Claude.Tests.StreamTests;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>Typed output through Claude's structured output (OUT-01), with the schema adjusted as spike finding 6 found it needs.</summary>
public class OutputTests
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public enum Mood
    {
        Calm,
        Busy,
    }

    public sealed record Line([property: Description("The book's id.")] int BookId, int Copies);

    public sealed record Order(string Customer, Mood Mood, IReadOnlyList<Line> Lines, Line? Gift, decimal Total);

    /// <summary>A hand-written schema with every keyword the adjustment changes, and a property named like one of them.</summary>
    private const string Written = """
        {"type":"object","properties":{
          "count":{"type":"integer","minimum":1,"maximum":9,"exclusiveMinimum":0,"exclusiveMaximum":10,"multipleOf":1},
          "maximum":{"type":"number"},
          "tags":{"type":"array","items":{"type":"string","pattern":"^[a-z]+$","minLength":1},"minItems":1,"maxItems":3},
          "pairs":{"type":"array","items":{"type":"object","properties":{"a":{"type":"string"}}},"minItems":2},
          "note":{"anyOf":[{"type":"null"},{"type":["object","null"],"properties":{"text":{"type":"string"}}}]}},
         "required":["count"]}
        """;

    private static ModelRequest Typed(string schema) => Hi with { OutputSchema = schema };

    private static ClaudeModel Offline() => new("test-key") { Model = "claude-opus-5-5", Effort = ClaudeEffort.Low };

    [Fact]
    public void A_written_schema_is_closed_and_stripped_of_what_the_API_rejects()
    {
        using var model = Offline();

        var config = ClaudeRequest.Build(model, Typed(Written)).RawBodyData["output_config"];

        Assert.Equal(Golden("output-written.json"), JsonSerializer.Serialize(config, Indented).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void An_open_object_is_refused_rather_than_closed()
    {
        using var model = Offline();
        const string dictionary = """{"type":"object","properties":{"counts":{"type":"object","additionalProperties":{"type":"integer"}}},"additionalProperties":false}""";

        Assert.Throws<ArgumentException>(() => ClaudeRequest.Build(model, Typed(dictionary)));
    }

    [Fact]
    public void An_exported_type_s_schema_is_sent_adjusted_as_the_output_format_and_tool_choice_is_never_forced()
    {
        using var model = Offline();

        var body = ClaudeRequest.Build(model, Typed(OutputContract.For<Order>().Schema)).RawBodyData;

        Assert.Equal(Golden("output-exported.json"), JsonSerializer.Serialize(body["output_config"], Indented).ReplaceLineEndings("\n"));
        Assert.False(body.ContainsKey("tool_choice"));
    }

    [Fact]
    public async Task A_structured_reply_comes_back_as_the_typed_output()
    {
        var reply = """{\"customer\":\"Ana\",\"mood\":\"Busy\",\"lines\":[{\"bookId\":144,\"copies\":2}],\"gift\":null,\"total\":12.56}""";
        var api = new FakeApi().Stream(Sse.Text("end_turn", reply));
        using var model = Model(api);
        var agent = new Agent { Model = model, Instructions = "Read the order.", Output = OutputContract.For<Order>() };

        var result = await agent.RunAsync("Ana wants two copies of book 144.", cancellationToken: TestContext.Current.CancellationToken);

        var order = Assert.IsType<Order>(Assert.IsType<Completed>(result).Output);
        Assert.Equal(new Line(144, 2), Assert.Single(order.Lines));
        Assert.Equal((Mood.Busy, 12.56m, null), (order.Mood, order.Total, order.Gift));
        var sent = JsonDocument.Parse(Assert.Single(api.Requests)).RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", sent.GetProperty("type").GetString());
    }

    private static string Golden(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).ReplaceLineEndings("\n").TrimEnd('\n');
}
