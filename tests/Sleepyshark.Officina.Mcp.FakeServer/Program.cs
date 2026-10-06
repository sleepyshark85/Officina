// The test kit's fake MCP server over stdio, as a program the MCP tests start. Its tools: echo (annotated read-only),
// upper (no annotations), fail (an error result), token (the credential in FAKE_MCP_TOKEN) and crash (the process
// exits mid-call).
using System.Globalization;
using Sleepyshark.Officina.Testing;

// With "silent <file>", it writes its process id to the file and never answers, until its input ends.
if (args is ["silent", var processIdFile])
{
    // Written aside and then moved, so the test never reads the file while it is open (Windows refuses that).
    await File.WriteAllTextAsync(processIdFile + ".tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
    File.Move(processIdFile + ".tmp", processIdFile);
    await Console.In.ReadToEndAsync();
    return;
}

using var server = new FakeMcpServer(
    new FakeMcpTool("echo", input => input.GetProperty("text").GetString()!) { ReadOnly = true },
    new FakeMcpTool("upper", input => input.GetProperty("text").GetString()!.ToUpperInvariant()),
    new FakeMcpTool("fail", _ => throw new InvalidOperationException("it broke")),
    new FakeMcpTool("token", _ => $"my token is {Environment.GetEnvironmentVariable("FAKE_MCP_TOKEN")}"),
    new FakeMcpTool("crash", _ =>
    {
        Environment.Exit(3);
        return "";
    }));
await server.ServeAsync(Console.In, Console.Out);
