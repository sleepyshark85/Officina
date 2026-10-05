// The test kit's fake MCP server over stdio (TEST-01), as a program the MCP tests start. Its tools: echo (annotated
// read-only), upper (no annotations), fail (an error result), token (the credential it was given in FAKE_MCP_TOKEN) and
// crash (the process exits mid-call, as a server that fails).
using Sleepyshark.Officina.Testing;

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
