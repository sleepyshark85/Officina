using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Sleepyshark.Officina.Sandbox;

/// <summary>
/// The filtering proxy, the only way out of a sandbox (SBX-01). It runs on the host for one command and dials only the
/// hosts on its allow list: HTTPS through <c>CONNECT host:port</c>, and plain HTTP through absolute URLs. It listens on a
/// Unix socket mounted into the Linux sandbox, or on a named pipe only the Windows sandbox's container may open.
/// </summary>
internal sealed class FilterProxy : IAsyncDisposable
{
    private static readonly byte[] Forbidden = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    private static readonly byte[] Established = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");

    private readonly IReadOnlyList<string> allowedHosts;
    private readonly Action<string> report;
    private readonly Func<CancellationToken, Task<Stream>> accept;
    private readonly Action close;
    private readonly CancellationTokenSource stop = new();
    private readonly Task accepting;

    private FilterProxy(IReadOnlyList<string> allowedHosts, Action<string> report, Func<CancellationToken, Task<Stream>> accept, Action close)
    {
        this.allowedHosts = allowedHosts;
        this.report = report;
        this.accept = accept;
        this.close = close;
        accepting = AcceptAsync();
    }

    /// <param name="path">The socket's path.</param>
    /// <param name="allowedHosts">The hosts the proxy dials.</param>
    /// <param name="report">Where each decision is told, as a line of the command's output.</param>
    public static FilterProxy OnUnixSocket(string path, IReadOnlyList<string> allowedHosts, Action<string> report)
    {
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen();
        return new(allowedHosts, report, async ct => new NetworkStream(await listener.AcceptAsync(ct).ConfigureAwait(false), ownsSocket: true), () =>
        {
            listener.Dispose();
            File.Delete(path);
        });
    }

    /// <param name="name">The pipe's name.</param>
    /// <param name="client">The only one besides the host who may open the pipe: the sandbox's container.</param>
    /// <param name="allowedHosts">The hosts the proxy dials.</param>
    /// <param name="report">Where each decision is told, as a line of the command's output.</param>
    [SupportedOSPlatform("windows")]
    public static FilterProxy OnNamedPipe(string name, SecurityIdentifier client, IReadOnlyList<string> allowedHosts, Action<string> report)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(client, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return new(allowedHosts, report, async ct =>
        {
            var pipe = NamedPipeServerStreamAcl.Create(
                name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }, () => { });
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await accepting.ConfigureAwait(false);
        close();
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
            Stream client;
            try
            {
                client = await accept(stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = ForwardAsync(client);
        }
    }

    private async Task ForwardAsync(Stream client)
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
            if (!Allows(target.Host) || target.Port < 0)
            {
                report($"[Network: refused {target.Host}, which is not an allowed host.]");
                await client.WriteAsync(Forbidden, stop.Token).ConfigureAwait(false);
                return;
            }

            report($"[Network: allowed {target.Host}:{target.Port}.]");

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

            var upload = PipeAsync(client, server);
            await PipeAsync(server, client).ConfigureAwait(false);
            report($"[debug download done {DateTime.Now:HH:mm:ss.fff}]");

            // A named pipe cannot be half-closed, so for a pipe the server's end is the connection's end.
            if (client is NetworkStream)
            {
                await upload.ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or UriFormatException or OperationCanceledException)
        {
            // The connection ends; the command sees it fail.
            report($"[debug {exception.GetType().Name} {DateTime.Now:HH:mm:ss.fff}]");
        }
        finally
        {
            // Disconnecting ends a pipe at once; disposing it would wait for a pending read, which only the other end can finish.
            if (client is NamedPipeServerStream { IsConnected: true } pipe)
            {
                pipe.Disconnect();
            }

            report($"[debug finally {DateTime.Now:HH:mm:ss.fff}]");
        }
    }

    /// <summary>Copies one direction until it ends, then tells the other end, which may still answer.</summary>
    private async Task PipeAsync(Stream from, Stream to)
    {
        await from.CopyToAsync(to, stop.Token).ConfigureAwait(false);
        (to as NetworkStream)?.Socket.Shutdown(SocketShutdown.Send);
    }

    /// <summary>The request line and headers, up to the empty line that ends them; null if the client stops first.</summary>
    private async Task<string?> ReadHeaderAsync(Stream client)
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
