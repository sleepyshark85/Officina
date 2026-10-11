using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// The Streamable HTTP transport: each message is a POST, answered with a JSON body or an event stream ending in the
/// response. A session id the server gives is sent back with every later message; a 404 for it loses the connection, as
/// the protocol asks the client to start a new session.
/// </summary>
internal sealed class HttpConnection(McpServer server, Action<string> lost) : McpConnection(server, lost)
{
    // A run's cancellation ends a call, not the client's own timeout.
    private readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? session;

    public override ValueTask DisposeAsync()
    {
        Closing = true;
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override async Task<JsonElement?> SendAsync(JsonObject message, int? id, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Server.Url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", Version);
        if (session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
        }

        foreach (var (name, value) in Server.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && session is not null)
        {
            throw new IOException("the server ended the session.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var given))
        {
            session = given.First();
        }

        if (id is null)
        {
            return null;
        }

        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.Clone();
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(line.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5)).Append('\n');
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                // An event ends at a blank line; the server's requests and notifications before the response are skipped.
                using var document = JsonDocument.Parse(data.ToString());
                if (IsResponse(document.RootElement, out var answered) && answered == id)
                {
                    return document.RootElement.Clone();
                }

                data.Clear();
            }
        }

        throw new IOException("the server ended its event stream without a response.");
    }
}
