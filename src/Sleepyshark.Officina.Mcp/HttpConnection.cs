using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sleepyshark.Officina.Mcp;

/// <summary>
/// The Streamable HTTP transport: each message is a POST, and the server answers a request with a JSON body or with an
/// event stream that ends in the response. A session id the server gives is sent back with every later message.
/// </summary>
/// <param name="url">The server's endpoint.</param>
/// <param name="headers">Headers sent with every message, such as credentials.</param>
internal sealed class HttpConnection(Uri url, IReadOnlyDictionary<string, string> headers) : McpConnection
{
    // The tool pipeline's time limit ends a call, not the client's.
    private readonly HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
    private string? session;

    public override ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    protected override async Task<JsonElement?> SendAsync(JsonObject message, int? id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
        if (session is not null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return document.RootElement.Clone();
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.Append(line.AsSpan(line.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5)).Append('\n');
            }
            else if (line.Length == 0 && data.Length > 0)
            {
                // An event ends at a blank line. Notifications before the response are skipped.
                using var document = JsonDocument.Parse(data.ToString());
                if (IsResponse(document.RootElement, id.Value))
                {
                    return document.RootElement.Clone();
                }

                data.Clear();
            }
        }

        throw new IOException("The tool server ended its event stream without a response.");
    }
}
