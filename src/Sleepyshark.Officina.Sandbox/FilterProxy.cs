using System.Net.Sockets;
using System.Text;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The filtering proxy, the only way out of a sandbox (SBX-01). It runs on the host for one command and dials only the
/// hosts on its allow list: HTTPS through <c>CONNECT host:port</c>, and plain HTTP through absolute URLs. It listens on a
/// Unix socket that is mounted into the sandbox.
/// </summary>
internal sealed class FilterProxy : IAsyncDisposable
{
    private static readonly byte[] Forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    private static readonly byte[] Established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");

    private readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly string socketPath;
    private readonly IReadOnlyList<string> allowedHosts;
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;

    public FilterProxy(string socketPath, IReadOnlyList<string> allowedHosts)
    {
        this.socketPath = socketPath;
        this.allowedHosts = allowedHosts;
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen();
        accepting = AcceptAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        listener.Dispose();
        await accepting.ConfigureAwait(false);
        File.Delete(socketPath);
        stop.Dispose();
    }

    /// <summary>Whether a host is on the allow list, where <c>*.example.com</c> allows every subdomain of example.com.</summary>
    private bool Allows(string host) => allowedHosts.Any(allowed => allowed.StartsWith("*.", StringComparison.Ordinal)
        ? host.EndsWith(allowed[1..], StringComparison.OrdinalIgnoreCase)
        : host.Equals(allowed, StringComparison.OrdinalIgnoreCase));

    private async Task AcceptAsync()
    {
        while (true)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = ForwardAsync(new NetworkStream(client, ownsSocket: true));
        }
    }

    private async Task ForwardAsync(NetworkStream client)
    {
        await using var _ = client.ConfigureAwait(false);
        try
        {
            // "CONNECT host:443 HTTP/1.1" for HTTPS, or "GET http://host/path HTTP/1.1" for plain HTTP.
            if (await ReadHeaderAsync(client).ConfigureAwait(false) is not { } header)
            {
                return;
            }

            var firstLine = header.IndexOf('\r', StringComparison.Ordinal);
            var request = header[..firstLine].Split(' ');
            if (request.Length != 3)
            {
                return;
            }

            var connect = request[0] == "CONNECT";
            var target = new Uri(connect ? $"tcp://{request[1]}" : request[1]);
            if (!Allows(target.Host))
            {
                await client.WriteAsync(Forbidden, stop.Token).ConfigureAwait(false);
                return;
            }

            using var upstream = new TcpClient();
            await upstream.ConnectAsync(target.Host, target.Port, stop.Token).ConfigureAwait(false);
            var server = upstream.GetStream();
            if (connect)
            {
                await client.WriteAsync(Established, stop.Token).ConfigureAwait(false);
            }
            else
            {
                // The server expects the path only, not the absolute URL.
                var forwarded = $"{request[0]} {target.PathAndQuery} {request[2]}{header[firstLine..]}";
                await server.WriteAsync(Encoding.ASCII.GetBytes(forwarded), stop.Token).ConfigureAwait(false);
            }

            await Task.WhenAll(PipeAsync(client, server), PipeAsync(server, client)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UriFormatException or OperationCanceledException)
        {
            // The connection ends; the command sees it fail.
        }
    }

    /// <summary>Copies one direction until it ends, then tells the other end, which may still answer.</summary>
    private async Task PipeAsync(NetworkStream from, NetworkStream to)
    {
        await from.CopyToAsync(to, stop.Token).ConfigureAwait(false);
        to.Socket.Shutdown(SocketShutdown.Send);
    }

    /// <summary>The request line and headers, up to the empty line that ends them; null if the client stops first.</summary>
    private async Task<string?> ReadHeaderAsync(NetworkStream client)
    {
        var header = new List<byte>();
        var next = new byte[1];
        while (header.Count < 16_384 && await client.ReadAsync(next, stop.Token).ConfigureAwait(false) == 1)
        {
            header.Add(next[0]);
            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
            {
                return Encoding.ASCII.GetString([.. header]);
            }
        }

        return null;
    }
}
