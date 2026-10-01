using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

// The reference MCP server of the tests (DESIGN.md §11). With no arguments it serves over standard input and output;
// with "http" it serves Streamable HTTP on a free local port, and prints its URL first. It accepts only the credential
// "s3cret": in MCP_TEST_TOKEN for stdio, as a bearer token for HTTP. It lists its tools one per page, in no particular
// order, and sends a notification before each tool result, which clients must skip. It agrees to the protocol version
// the client asks for, unless MCP_TEST_PROTOCOL names another.
const string Credential = "s3cret";
const string Notification = """{ "jsonrpc": "2.0", "method": "notifications/message", "params": { "level": "info", "data": "working" } }""";
(string Name, string Description)[] tools = [("upper", "Upper-cases text."), ("echo", "Repeats text."), ("fail", "Always fails.")];

if (args is ["http"])
{
    await ServeHttpAsync();
}
else
{
    await ServeStdioAsync();
}

async Task ServeStdioAsync()
{
    var authorized = Environment.GetEnvironmentVariable("MCP_TEST_TOKEN") == Credential;
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonNode.Parse(line)!.AsObject();
        if ((string?)request["method"] == "tools/call")
        {
            Console.WriteLine(Notification);
        }

        if (Handle(request, authorized) is { } response)
        {
            Console.WriteLine(response.ToJsonString());
        }
    }
}

async Task ServeHttpAsync()
{
    var free = new TcpListener(IPAddress.Loopback, 0);
    free.Start();
    var url = $"http://localhost:{((IPEndPoint)free.LocalEndpoint).Port}/mcp/";
    free.Stop();
    using var listener = new HttpListener();
    listener.Prefixes.Add(url);
    listener.Start();
    Console.WriteLine(url);
    var session = Guid.NewGuid().ToString();
    while (true)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        var request = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        var response = context.Response;
        if (context.Request.Headers["Authorization"] != $"Bearer {Credential}")
        {
            response.StatusCode = 401;
        }
        else if ((string?)request["method"] != "initialize" && context.Request.Headers["Mcp-Session-Id"] != session)
        {
            response.StatusCode = 400;
        }
        else if (Handle(request, authorized: true) is not { } answer)
        {
            response.StatusCode = 202;
        }
        else
        {
            response.Headers["Mcp-Session-Id"] = session;
            var stream = (string?)request["method"] == "tools/call";
            response.ContentType = stream ? "text/event-stream" : "application/json";
            var body = stream ? $"data: {Notification}\n\ndata: {answer.ToJsonString()}\n\n" : answer.ToJsonString();
            await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body));
        }

        response.Close();
    }
}

JsonObject? Handle(JsonObject request, bool authorized)
{
    if (request["id"] is not { } id)
    {
        return null;
    }

    var parameters = request["params"];
    JsonNode? result = (string?)request["method"] switch
    {
        "initialize" when authorized => new JsonObject
        {
            ["protocolVersion"] = Environment.GetEnvironmentVariable("MCP_TEST_PROTOCOL") ?? (string?)parameters!["protocolVersion"],
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "reference", ["version"] = "1.0.0" },
        },
        "tools/list" => List(parameters?["cursor"] is { } cursor ? int.Parse((string)cursor!, CultureInfo.InvariantCulture) : 0),
        "tools/call" => Call((string)parameters!["name"]!, (string?)parameters["arguments"]?["text"] ?? ""),
        _ => null,
    };
    return result is null
        ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "not supported" } }
        : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
}

JsonObject List(int page)
{
    var tool = new JsonObject
    {
        ["name"] = tools[page].Name,
        ["description"] = tools[page].Description,
        ["inputSchema"] = JsonNode.Parse("""{ "type": "object", "properties": { "text": { "type": "string" } }, "required": ["text"] }"""),
    };
    var result = new JsonObject { ["tools"] = new JsonArray(tool) };
    if (page + 1 < tools.Length)
    {
        result["nextCursor"] = (page + 1).ToString(CultureInfo.InvariantCulture);
    }

    return result;
}

static JsonObject Call(string name, string text) => new()
{
    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = name switch { "upper" => text.ToUpperInvariant(), "fail" => "it broke", _ => text } }),
    ["isError"] = name == "fail",
};
