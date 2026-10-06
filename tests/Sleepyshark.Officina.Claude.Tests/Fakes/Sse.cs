namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>Server-sent events for the tests, in the shape the Claude API streams them.</summary>
internal static class Sse
{
    public const string Start = """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":10,"output_tokens":1}}}""";

    public static string Events(params string[] data) =>
        string.Concat(data.Select(json => $"event: {Type(json)}\ndata: {json}\n\n"));

    /// <summary>A whole reply of one text block, streamed in pieces, that stops for <paramref name="reason"/>.</summary>
    public static string Text(string reason = "end_turn", params string[] pieces) => Events(
    [
        Start,
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        .. (pieces.Length == 0 ? ["Hello."] : pieces).Select(piece => $$$"""{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"{{{piece}}}"}}"""),
        """{"type":"content_block_stop","index":0}""",
        $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{reason}}}","stop_sequence":null},"usage":{"output_tokens":5}}""",
        """{"type":"message_stop"}""",
    ]);

    public static string Error(string type) => $$$"""{"type":"error","error":{"type":"{{{type}}}","message":"Overloaded"}}""";

    private static string Type(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("type").GetString()!;
}
