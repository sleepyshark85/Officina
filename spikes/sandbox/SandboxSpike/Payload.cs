using System.Diagnostics;

namespace SandboxSpike;

/// <summary>Misbehaving programs run inside the sandbox to exercise the limits.</summary>
public static class Payload
{
    public static int Run(string[] a)
    {
        switch (a[0])
        {
            case "membomb":
            {
                var keep = new List<byte[]>();
                for (int i = 0; ; i++)
                {
                    var b = new byte[32 << 20];
                    for (int j = 0; j < b.Length; j += 4096) b[j] = 1; // touch every page
                    keep.Add(b);
                    Console.WriteLine($"allocated {(i + 1) * 32} MB");
                }
            }
            case "spin":
            {
                int threads = int.Parse(a[1]); var secs = double.Parse(a[2], System.Globalization.CultureInfo.InvariantCulture);
                var sw = Stopwatch.StartNew();
                var ts = Enumerable.Range(0, threads).Select(_ => new Thread(() => { while (sw.Elapsed.TotalSeconds < secs) { } })).ToList();
                ts.ForEach(t => t.Start()); ts.ForEach(t => t.Join());
                var cpu = Process.GetCurrentProcess().TotalProcessorTime.TotalSeconds;
                Console.WriteLine(FormattableString.Invariant($"CORES_USED={cpu / sw.Elapsed.TotalSeconds:F2} threads={threads} wall={sw.Elapsed.TotalSeconds:F1}s"));
                return 0;
            }
            case "spawn":
            {
                int n = int.Parse(a[1]), ok = 0; string? err = null;
                var self = Environment.ProcessPath!; var dll = typeof(Payload).Assembly.Location;
                var kids = new List<Process>();
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        var psi = new ProcessStartInfo(self) { UseShellExecute = false };
                        psi.ArgumentList.Add(dll); psi.ArgumentList.Add("payload"); psi.ArgumentList.Add("sleep"); psi.ArgumentList.Add("60");
                        kids.Add(Process.Start(psi)!); ok++;
                    }
                    catch (Exception e) { err = e.Message; break; }
                }
                Console.WriteLine($"SPAWNED={ok} of {n}; first error: {err}");
                return 0;
            }
            case "sleep":
                Thread.Sleep(TimeSpan.FromSeconds(double.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture)));
                return 0;
            case "tree":
            {
                // Children that try to escape: a grandchild chain, all sleeping. Writes PIDs to a file.
                var self = Environment.ProcessPath!; var dll = typeof(Payload).Assembly.Location;
                int depth = a.Length > 1 ? int.Parse(a[1]) : 2;
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "tree.pids"), Environment.ProcessId + "\n");
                if (depth > 0)
                {
                    var psi = new ProcessStartInfo(self) { UseShellExecute = false };
                    foreach (var x in new[] { dll, "payload", "tree", (depth - 1).ToString() }) psi.ArgumentList.Add(x);
                    Process.Start(psi);
                }
                Thread.Sleep(TimeSpan.FromMinutes(10));
                return 0;
            }
            case "connect":
            {
                // Raw TCP connect attempt, bypassing any proxy settings.
                try
                {
                    using var c = new System.Net.Sockets.TcpClient();
                    c.ConnectAsync(a[1], int.Parse(a[2])).Wait(TimeSpan.FromSeconds(8));
                    Console.WriteLine(c.Connected ? "CONNECTED" : "NOT_CONNECTED (timeout)");
                    return c.Connected ? 0 : 1;
                }
                catch (Exception e) { Console.WriteLine("NOT_CONNECTED " + e.GetBaseException().Message); return 1; }
            }
            case "fetch":
            {
                // HTTPS GET through whatever HTTPS_PROXY says. Optional 2nd arg: file to wait for first.
                if (a.Length > 2) for (int i = 0; i < 100 && !File.Exists(a[2]); i++) Thread.Sleep(100);
                try
                {
                    using var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    var r = h.GetAsync(a[1]).GetAwaiter().GetResult();
                    Console.WriteLine($"HTTP {(int)r.StatusCode} via proxy={Environment.GetEnvironmentVariable("HTTPS_PROXY")}");
                    return r.IsSuccessStatusCode ? 0 : 1;
                }
                catch (Exception e) { Console.WriteLine("FETCH_FAILED " + e.GetBaseException().Message); return 1; }
            }
            case "forward":
                PipeForwarder.Run(int.Parse(a[1]), a[2], a[3]).GetAwaiter().GetResult();
                return 0;
            case "readfile":
            {
                try { Console.WriteLine("READ_OK " + File.ReadAllText(a[1])); return 0; }
                catch (Exception e) { Console.WriteLine("READ_FAILED " + e.GetType().Name + ": " + e.Message); return 1; }
            }
            case "writefile":
            {
                try { File.WriteAllText(a[1], "x"); Console.WriteLine("WRITE_OK"); return 0; }
                catch (Exception e) { Console.WriteLine("WRITE_FAILED " + e.GetType().Name + ": " + e.Message); return 1; }
            }
            default:
                Console.Error.WriteLine("unknown payload " + a[0]); return 2;
        }
    }
}
