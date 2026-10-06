using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Testing;

/// <summary>
/// An MCP server with tools given in advance, over stdio (<see cref="ServeAsync"/>, hosted by a test program) or
/// Streamable HTTP on a local port (<see cref="StartHttp"/>). It lists one tool per page, sends a notification before
/// each result (as an event stream over HTTP) that clients must skip, and records the calls. Over HTTP it can require a
/// bearer token and go <see cref="Down"/>.
/// </summary>
public sealed class FakeMcpServer : IDisposable
{
    private const string Notification = """{"jsonrpc":"2.0","method":"notifications/message","params":{"level":"info","data":"working"}}""";

    private readonly Lock gate = new();
    private readonly List<FakeMcpTool> tools;
    private readonly List<(string Tool, string Arguments)> calls = [];
    private readonly string session = Guid.NewGuid().ToString("N");
    private HttpListener? listener;

    public FakeMcpServer(params FakeMcpTool[] tools) => this.tools = [.. tools];

    /// <summary>The bearer token HTTP requests must carry; null for none.</summary>
    public string? Token { get; init; }

    /// <summary>While true, the HTTP server drops every request, as if down; a tool may set it to fail mid-call.</summary>
    public bool Down { get; set; }

    /// <summary>The HTTP endpoint, once started.</summary>
    public Uri? Url { get; private set; }

    /// <summary>The tool calls received, in order, with their arguments as JSON text.</summary>
    public IReadOnlyList<(string Tool, string Arguments)> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    /// <summary>Replaces the tools the server lists, as a server whose tools change between connections.</summary>
    public void SetTools(params FakeMcpTool[] replacement)
    {
        lock (gate)
        {
            tools.Clear();
            tools.AddRange(replacement);
        }
    }

    /// <summary>Serves over stdio, one JSON-RPC message per line, until <paramref name="input"/> ends.</summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var request = JsonNode.Parse(line)!.AsObject();
            if ((string?)request["method"] == "tools/call")
            {
                await output.WriteLineAsync(Notification).ConfigureAwait(false);
            }

            if (Handle(request) is { } response)
            {
                await output.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Serves Streamable HTTP on a free local port, and returns the endpoint.</summary>
    public Uri StartHttp()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        Url = new Uri($"http://localhost:{((IPEndPoint)free.LocalEndpoint).Port}/mcp/");
        free.Stop();
        listener = new HttpListener();
        listener.Prefixes.Add(Url.ToString());
        listener.Start();
        _ = Task.Run(ListenAsync);
        return Url;
    }

    public void Dispose() => (listener as IDisposable)?.Dispose();

    private async Task ListenAsync()
    {
        while (listener!.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(context));
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var response = context.Response;
        try
        {
            if (Down)
            {
                response.Abort();
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream);
            var request = JsonNode.Parse(await reader.ReadToEndAsync().ConfigureAwait(false))!.AsObject();
            var authorized = Token is null || context.Request.Headers["Authorization"] == $"Bearer {Token}";
            var answer = authorized ? Handle(request) : null;
            if (Down)
            {
                response.Abort();
                return;
            }

            if (!authorized)
            {
                response.StatusCode = 401;
            }
            else if ((string?)request["method"] != "initialize" && context.Request.Headers["Mcp-Session-Id"] != session)
            {
                response.StatusCode = 404;
            }
            else if (answer is null)
            {
                response.StatusCode = 202;
            }
            else
            {
                response.Headers["Mcp-Session-Id"] = session;
                var stream = (string?)request["method"] == "tools/call";
                response.ContentType = stream ? "text/event-stream" : "application/json";
                var body = stream ? $"data: {Notification}\n\ndata: {answer.ToJsonString()}\n\n" : answer.ToJsonString();
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(body)).ConfigureAwait(false);
            }

            response.Close();
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or JsonException)
        {
            response.Abort();
        }
    }

    /// <summary>The response to a request; null for a notification.</summary>
    private JsonObject? Handle(JsonObject request)
    {
        if (request["id"] is not { } id)
        {
            return null;
        }

        var parameters = request["params"];
        JsonNode? result = (string?)request["method"] switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = (string?)parameters?["protocolVersion"],
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1.0.0" },
            },
            "ping" => new JsonObject(),
            "tools/list" => List(parameters?["cursor"] is { } cursor ? int.Parse((string)cursor!, CultureInfo.InvariantCulture) : 0),
            "tools/call" => Call((string)parameters!["name"]!, parameters["arguments"]?.ToJsonString() ?? "{}"),
            _ => null,
        };
        return result is null
            ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method or tool not found." } }
            : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    }

    private JsonObject List(int page)
    {
        lock (gate)
        {
            var result = new JsonObject { ["tools"] = new JsonArray() };
            if (page < tools.Count)
            {
                var tool = tools[page];
                var listed = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = JsonNode.Parse(tool.InputSchema) };
                if (tool.ReadOnly is { } readOnly)
                {
                    listed["annotations"] = new JsonObject { ["readOnlyHint"] = readOnly };
                }

                result["tools"]!.AsArray().Add(listed);
            }

            if (page + 1 < tools.Count)
            {
                result["nextCursor"] = (page + 1).ToString(CultureInfo.InvariantCulture);
            }

            return result;
        }
    }

    private JsonObject? Call(string name, string arguments)
    {
        FakeMcpTool? tool;
        lock (gate)
        {
            calls.Add((name, arguments));
            tool = tools.FirstOrDefault(each => each.Name == name);
        }

        if (tool is null)
        {
            return null;
        }

        string text;
        var failed = false;
        try
        {
            using var input = JsonDocument.Parse(arguments);
            text = tool.Handler(input.RootElement);
        }
#pragma warning disable CA1031 // A failing tool is an error result, as a server reports it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            (text, failed) = (exception.Message, true);
        }

        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = failed,
        };
    }
}

/// <summary>A tool of a <see cref="FakeMcpServer"/>: its handler's text is the result; an exception is an error result.</summary>
/// <param name="Name">The tool's name on the server.</param>
/// <param name="Handler">Answers a call's arguments.</param>
public sealed record FakeMcpTool(string Name, Func<JsonElement, string> Handler)
{
    public string Description { get; init; } = $"The {Name} tool.";

    public string InputSchema { get; init; } = """{"type":"object","properties":{"text":{"type":"string"}}}""";

    /// <summary>The <c>readOnlyHint</c> annotation; null lists no annotations.</summary>
    public bool? ReadOnly { get; init; }
}
