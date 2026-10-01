using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SandboxSpike;

/// <summary>
/// Minimal host-side filtering proxy. Understands HTTP CONNECT (for HTTPS) and absolute-URI
/// plain HTTP requests. Only hosts on the allow list are dialled; everything else gets 403.
/// Listens on a Unix socket (Linux), a loopback TCP port, or a named pipe (Windows).
/// </summary>
public sealed class FilterProxy : IAsyncDisposable
{
    private readonly HashSet<string> _allow;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _loops = new();
    public ConcurrentQueue<string> Log { get; } = new();

    public FilterProxy(IEnumerable<string> allowHosts) =>
        _allow = new HashSet<string>(allowHosts, StringComparer.OrdinalIgnoreCase);

    public void ListenUnix(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Bind(new UnixDomainSocketEndPoint(path));
        s.Listen(64);
        _loops.Add(AcceptLoop(s));
    }

    public int ListenTcpLoopback(int port = 0)
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Loopback, port));
        s.Listen(64);
        _loops.Add(AcceptLoop(s));
        return ((IPEndPoint)s.LocalEndPoint!).Port;
    }

    /// <summary>Named pipe whose DACL is supplied by the caller (Windows only).</summary>
    public void ListenNamedPipe(Func<NamedPipeServerStream> factory) => _loops.Add(PipeLoop(factory));

    private async Task AcceptLoop(Socket listener)
    {
        using var lst = listener;
        while (!_cts.IsCancellationRequested)
        {
            Socket c;
            try { c = await listener.AcceptAsync(_cts.Token); } catch { return; }
            _ = Handle(new NetworkStream(c, ownsSocket: true));
        }
    }

    private async Task PipeLoop(Func<NamedPipeServerStream> factory)
    {
        while (!_cts.IsCancellationRequested)
        {
            var p = factory();
            try { await p.WaitForConnectionAsync(_cts.Token); } catch { p.Dispose(); return; }
            _ = Handle(p);
        }
    }

    private async Task Handle(Stream client)
    {
        await using var _ = client;
        try
        {
            var header = await ReadHeader(client);
            if (header is null) return;
            var firstLine = header.Split("\r\n")[0];
            var parts = firstLine.Split(' ');
            if (parts.Length < 3) return;
            string host; int port; bool connect = parts[0] == "CONNECT";
            if (connect)
            {
                var hp = parts[1];
                var i = hp.LastIndexOf(':');
                host = hp[..i]; port = int.Parse(hp[(i + 1)..]);
            }
            else
            {
                var uri = new Uri(parts[1]);
                host = uri.Host; port = uri.Port;
            }

            if (!_allow.Contains(host))
            {
                Log.Enqueue($"DENY {host}:{port}");
                await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                return;
            }
            Log.Enqueue($"ALLOW {host}:{port}");
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(host, port);
            var us = upstream.GetStream();
            if (connect)
                await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"));
            else
            {
                // Rewrite absolute URI to origin form and forward the header as is.
                var uri = new Uri(parts[1]);
                var rewritten = $"{parts[0]} {uri.PathAndQuery} {parts[2]}" + header[firstLine.Length..];
                await us.WriteAsync(Encoding.ASCII.GetBytes(rewritten));
            }
            var a = client.CopyToAsync(us);
            var b = us.CopyToAsync(client);
            await Task.WhenAny(a, b);
        }
        catch (Exception e) { Log.Enqueue($"ERR {e.GetType().Name}: {e.Message}"); }
    }

    private static async Task<string?> ReadHeader(Stream s)
    {
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 16384)
        {
            if (await s.ReadAsync(one) == 0) return null;
            buf.Add(one[0]);
            int n = buf.Count;
            if (n >= 4 && buf[n - 4] == '\r' && buf[n - 3] == '\n' && buf[n - 2] == '\r' && buf[n - 1] == '\n')
                return Encoding.ASCII.GetString(buf.ToArray());
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
    }
}

/// <summary>Tiny TCP-loopback → named-pipe forwarder, run *inside* the Windows sandbox (variant B).</summary>
public static class PipeForwarder
{
    public static async Task Run(int port, string pipeName, string readyFile)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        File.WriteAllText(readyFile, "ready");
        while (true)
        {
            var c = await l.AcceptTcpClientAsync();
            _ = Task.Run(async () =>
            {
                using var _ = c;
                await using var p = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await p.ConnectAsync(5000);
                var s = c.GetStream();
                await Task.WhenAny(s.CopyToAsync(p), p.CopyToAsync(s));
            });
        }
    }
}
