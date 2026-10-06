using System.Text.Json;
using Sleepyshark.Officina.Testing;
using Sleepyshark.Officina.Tests;

namespace Sleepyshark.Officina.Mcp.Tests;

/// <summary>
/// S11: the MCP tool source against the test kit's fake MCP server, over stdio (a child process) and Streamable HTTP
/// (in this process). Only the model, the server and the human are scripted; the core runs for real.
/// </summary>
public sealed class McpToolSourceTests
{
    private const string Token = "fake-token-73";

    private static readonly string FakeServerProgram = Path.Combine(AppContext.BaseDirectory, "Sleepyshark.Officina.Mcp.FakeServer.dll");

    private static McpServer StdioServer(string? token = null) => McpServer.Stdio(
        "fake", "dotnet", [FakeServerProgram], token is null ? null : new Dictionary<string, string> { ["FAKE_MCP_TOKEN"] = token });

    private static FakeMcpServer HttpServer(params FakeMcpTool[] extra) => new(
        [
            new FakeMcpTool("echo", input => input.GetProperty("text").GetString()!) { ReadOnly = true },
            new FakeMcpTool("upper", input => input.GetProperty("text").GetString()!.ToUpperInvariant()),
            .. extra,
        ])
    { Token = Token };

    private static McpServer HttpServerAt(FakeMcpServer fake, string token = Token) =>
        McpServer.Http("fake", fake.StartHttp(), new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" });

    private static Agent AgentOf(ScriptedModel model, McpToolSource source, McpServer server, IApprover? approver = null, IAuditSink? audit = null) => new()
    {
        Name = "mcp-test",
        Model = model,
        Instructions = "You use tools.",
        Tools = source.Tools,
        Approver = approver,
        AuditSink = audit,
        Secrets = server.Secrets,
    };

    private static ToolCall Call(string id, string name, object input) => new(id, name, JsonSerializer.Serialize(input));

    private static IReadOnlyList<ToolResult> LastResults(ScriptedModel model) =>
        [.. model.Requests[^1].Messages[^1].Blocks.Select(block => block.ToolResult!)];

    private static IEnumerable<string> SourceChanges(RecordingSink sink) =>
        sink.Entries.Where(entry => entry.Kind == AuditKind.ToolSource).Select(entry => $"{entry.Tool} {entry.Outcome}");

    [Fact]
    public async Task MCP_01_a_stdio_server_s_tools_run_and_their_results_reach_the_model()
    {
        var server = StdioServer();
        await using var source = await McpToolSource.ConnectAsync(server, [new("echo"), new("upper"), new("fail")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel()
            .CallTools(Call("c1", "fake__upper", new { text = "hi" }), Call("c2", "fake__echo", new { text = "there" }), Call("c3", "fake__fail", new { }))
            .Reply("Done.");

        var result = await AgentOf(model, source, server).RunAsync(new Conversation(), "Go.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<Completed>(result);
        Assert.Equal(
            [new ToolResult("c1", "HI", false), new ToolResult("c2", "there", false), new ToolResult("c3", "it broke", IsError: true)],
            LastResults(model));
    }

    [Fact]
    public async Task MCP_01_an_http_server_s_tools_run_with_the_credential_the_host_gives()
    {
        using var fake = HttpServer();
        var server = HttpServerAt(fake);
        await using var source = await McpToolSource.ConnectAsync(server, [new("upper")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel().CallTools(Call("c1", "fake__upper", new { text = "hi" })).Reply("Done.");

        var result = await AgentOf(model, source, server).RunAsync(new Conversation(), "Go.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<Completed>(result);
        Assert.Equal([new ToolResult("c1", "HI", false)], LastResults(model));
        Assert.Equal([("upper", """{"text":"hi"}""")], fake.Calls);
    }

    [Fact]
    public async Task MCP_02_MCP_03_only_allowed_tools_appear_named_by_server_and_tool_and_are_writes_unless_the_host_marks_them_read()
    {
        using var fake = HttpServer(new FakeMcpTool("delete_everything", _ => "gone"));
        var server = HttpServerAt(fake);

        await using var source = await McpToolSource.ConnectAsync(server, [new("echo"), new("upper")], TestContext.Current.CancellationToken);
        await using var overridden = await McpToolSource.ConnectAsync(
            server, [new("echo", ToolKind.Write, NeedsApproval: true), new("upper", ToolKind.Read)], TestContext.Current.CancellationToken);

        Assert.Equal(["fake__echo", "fake__upper"], source.Tools.Select(tool => tool.Name));
        // The fake server marks echo read-only; the annotation is not trusted.
        Assert.Equal([ToolKind.Write, ToolKind.Write], source.Tools.Select(tool => tool.Kind));
        Assert.Equal([ToolKind.Write, ToolKind.Read], overridden.Tools.Select(tool => tool.Kind));
        Assert.Equal([true, false], overridden.Tools.Select(tool => tool.NeedsApproval));
        Assert.All(source.Tools, tool => Assert.Same(source, tool.Source));

        // The model is offered the allowed tools only, as the server lists them.
        var model = new ScriptedModel().Reply("Hello.");
        await AgentOf(model, source, server).RunAsync(new Conversation(), "Hi.", cancellationToken: TestContext.Current.CancellationToken);
        var offered = Assert.Single(model.Requests).Prefix.Tools;
        Assert.Equal(["fake__echo", "fake__upper"], offered.Select(tool => tool.Name));
        Assert.Equal("The echo tool.", offered[0].Description);
        Assert.Equal("""{"type":"object","properties":{"text":{"type":"string"}}}""", offered[0].InputSchema);
    }

    [Fact]
    public async Task MCP_03_an_allowed_tool_the_server_lacks_fails_the_connection_clearly()
    {
        using var fake = HttpServer();
        var server = HttpServerAt(fake);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => McpToolSource.ConnectAsync(server, [new("echo"), new("write_file")], TestContext.Current.CancellationToken));

        Assert.Equal("The MCP server 'fake' has no tool 'write_file'. It has: echo, upper.", error.Message);
    }

    [Fact]
    public async Task MCP_03_CTX_04_the_tool_list_is_read_once_and_pinned_for_the_conversation()
    {
        using var fake = HttpServer();
        var server = HttpServerAt(fake);
        await using var source = await McpToolSource.ConnectAsync(server, [new("echo")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel().Reply("One.").Reply("Two.").Reply("Three.");
        var agent = AgentOf(model, source, server);
        var conversation = new Conversation();
        await agent.RunAsync(conversation, "First.", cancellationToken: TestContext.Current.CancellationToken);

        // The server changes its tools, and even loses its connection, which the next run makes anew.
        fake.SetTools(new FakeMcpTool("echo", _ => "") { Description = "A new description." });
        await agent.RunAsync(conversation, "Second.", cancellationToken: TestContext.Current.CancellationToken);
        fake.Down = true;
        await agent.RunAsync(conversation, "Down.", cancellationToken: TestContext.Current.CancellationToken);
        fake.Down = false;
        await agent.RunAsync(conversation, "Third.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, model.Requests.Count);
        Assert.All(model.Requests, request => Assert.Equal("The echo tool.", Assert.Single(request.Prefix.Tools).Description));
        Assert.Empty(PrefixStability.Problems(model.Requests));

        // A source connected now reads the changed list: an agent built with it is another prefix, so it starts a new conversation.
        await using var changed = await McpToolSource.ConnectAsync(server, [new("echo")], TestContext.Current.CancellationToken);
        var rebuilt = AgentOf(new ScriptedModel().Reply("Four."), changed, server);
        Assert.False(rebuilt.CanContinue(conversation));
        var mismatch = Assert.IsType<Failed>(await rebuilt.RunAsync(conversation, "Fourth.", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(FailureReason.PrefixMismatch, mismatch.Reason);
    }

    [Fact]
    public async Task MCP_02_an_mcp_tool_goes_through_validation_approval_audit_truncation_and_events()
    {
        using var fake = HttpServer(new FakeMcpTool("big", _ => new string('x', 70_000)));
        var server = HttpServerAt(fake);
        await using var source = await McpToolSource.ConnectAsync(
            server, [new("upper", NeedsApproval: true), new("big")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel()
            .CallTools(Call("c1", "fake__upper", new { text = 5 }))
            .CallTools(Call("c2", "fake__upper", new { text = "no" }))
            .CallTools(Call("c3", "fake__upper", new { text = "yes" }), Call("c4", "fake__big", new { }))
            .Reply("Done.");
        var approver = new ScriptedApprover().Answer(Approval.Denied("not now"), Approval.Granted);
        var sink = new RecordingSink();
        using var telemetry = new TelemetryCollector();
        var events = new List<RunEvent>();

        await foreach (var runEvent in AgentOf(model, source, server, approver, sink).StreamAsync(new Conversation(), "Go.", cancellationToken: TestContext.Current.CancellationToken))
        {
            events.Add(runEvent);
        }

        // Invalid input and a denied call never reach the server; the approved one does.
        Assert.Equal([("upper", """{"text":"yes"}"""), ("big", "{}")], fake.Calls);
        var results = model.Requests.Skip(1).Select(request => request.Messages[^1].Blocks[0].ToolResult!).ToList();
        Assert.StartsWith("The input does not match the tool's schema:", results[0].Content, StringComparison.Ordinal);
        Assert.Equal(new ToolResult("c2", "The call was denied: not now", IsError: true), results[1]);
        Assert.Equal(new ToolResult("c3", "YES", false), results[2]);
        Assert.Equal(["c2", "c3"], approver.Asked.Select(call => call.Id));
        Assert.EndsWith("[Truncated: the result had 70000 characters; only the first 64000 are shown.]", LastResults(model)[1].Content, StringComparison.Ordinal);

        Assert.Equal(["c1", "c2", "c3", "c4"], events.OfType<ToolCallFinished>().Select(finished => finished.Call.Id).Order(StringComparer.Ordinal));
        Assert.Equal(2, events.OfType<ApprovalAsked>().Count());
        Assert.Equal(
            [AuditKind.ToolStarted, AuditKind.ToolEnded],
            sink.Entries.Where(entry => entry.CallId == "c3" && entry.Kind is AuditKind.ToolStarted or AuditKind.ToolEnded).Select(entry => entry.Kind));
        Assert.All(
            telemetry.Spans("mcp-test").Where(span => span.OperationName.StartsWith("execute_tool", StringComparison.Ordinal)),
            span => Assert.Equal("fake", span.GetTagItem("officina.tool.source")));
    }

    [Fact]
    public async Task MCP_04_a_server_down_at_the_start_of_a_run_fails_it_clearly_and_the_next_run_reconnects()
    {
        using var fake = HttpServer();
        var server = HttpServerAt(fake);
        await using var source = await McpToolSource.ConnectAsync(server, [new("echo")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel().Reply("Back.");
        var sink = new RecordingSink();
        var agent = AgentOf(model, source, server, audit: sink);
        var conversation = new Conversation();
        fake.Down = true;

        var failed = Assert.IsType<Failed>(await agent.RunAsync(conversation, "Hi.", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(FailureReason.ToolSourceUnavailable, failed.Reason);
        Assert.StartsWith("The tool source 'fake' is not available: The MCP server 'fake' could not be reached: ", failed.Error, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        Assert.Empty(conversation.Messages);

        fake.Down = false;
        Assert.IsType<Completed>(await agent.RunAsync(conversation, "Hi again.", cancellationToken: TestContext.Current.CancellationToken));

        // AUD-01: the host's first connection, the loss, the failed attempt, and the new connection.
        Assert.Equal(["fake connected", "fake disconnected", "fake failed", "fake connected"], SourceChanges(sink));
    }

    [Fact]
    public async Task MCP_04_an_http_server_that_fails_mid_run_gives_error_results_and_the_run_goes_on()
    {
        FakeMcpServer? server = null;
        using var fake = server = HttpServer(new FakeMcpTool("drop", _ =>
        {
            server!.Down = true;
            return "never sent";
        }));
        var mcp = HttpServerAt(fake);
        await using var source = await McpToolSource.ConnectAsync(mcp, [new("drop"), new("echo")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel()
            .CallTools(Call("c1", "fake__drop", new { }))
            .CallTools(Call("c2", "fake__echo", new { text = "anyone?" }))
            .Reply("The file server is down.");
        var sink = new RecordingSink();

        var result = await AgentOf(model, source, mcp, audit: sink).RunAsync(new Conversation(), "Go.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.IsType<Completed>(result);
        var first = model.Requests[1].Messages[^1].Blocks[0].ToolResult!;
        var second = model.Requests[2].Messages[^1].Blocks[0].ToolResult!;
        Assert.True(first.IsError);
        Assert.StartsWith("The MCP server 'fake' could not be reached: ", first.Content, StringComparison.Ordinal);
        Assert.True(second.IsError);
        Assert.StartsWith("The MCP server 'fake' is not connected: The MCP server 'fake' could not be reached: ", second.Content, StringComparison.Ordinal);
        Assert.Equal(["fake connected", "fake disconnected"], SourceChanges(sink));
    }

    [Fact]
    public async Task MCP_04_a_stdio_server_that_exits_mid_run_gives_an_error_result_and_the_next_run_starts_it_again()
    {
        var server = StdioServer();
        await using var source = await McpToolSource.ConnectAsync(server, [new("crash"), new("echo")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel()
            .CallTools(Call("c1", "fake__crash", new { }))
            .Reply("It crashed.")
            .CallTools(Call("c2", "fake__echo", new { text = "back" }))
            .Reply("It is back.");
        var sink = new RecordingSink();
        var agent = AgentOf(model, source, server, audit: sink);
        var conversation = new Conversation();

        Assert.IsType<Completed>(await agent.RunAsync(conversation, "Crash it.", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new ToolResult("c1", "The MCP server 'fake' closed its connection.", IsError: true), model.Requests[1].Messages[^1].Blocks[0].ToolResult);

        Assert.IsType<Completed>(await agent.RunAsync(conversation, "Again.", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new ToolResult("c2", "back", false), LastResults(model)[0]);
        Assert.Equal(["fake connected", "fake disconnected", "fake connected"], SourceChanges(sink));
    }

    [Fact]
    public async Task MCP_04_a_stdio_server_that_cannot_start_fails_to_connect_clearly()
    {
        var error = await Assert.ThrowsAsync<IOException>(
            () => McpToolSource.ConnectAsync(McpServer.Stdio("fake", "no-such-program-officina"), [], TestContext.Current.CancellationToken));

        Assert.StartsWith("The MCP server 'fake' could not be started (no-such-program-officina): ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EVT_03_mcp_credentials_never_reach_the_model_events_audit_or_errors()
    {
        var server = StdioServer(Token);
        await using var source = await McpToolSource.ConnectAsync(server, [new("token")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel().CallTools(Call("c1", "fake__token", new { })).Reply("Done.");
        var sink = new RecordingSink();
        var events = new List<RunEvent>();

        await foreach (var runEvent in AgentOf(model, source, server, audit: sink).StreamAsync(new Conversation(), "Go.", cancellationToken: TestContext.Current.CancellationToken))
        {
            events.Add(runEvent);
        }

        Assert.Equal(new ToolResult("c1", "my token is [redacted]", false), LastResults(model)[0]);
        Assert.DoesNotContain(Token, string.Join("\n", events.Select(runEvent => runEvent.ToString())), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(sink.Entries), StringComparison.Ordinal);

        // A server that refuses the credential says so without it.
        using var fake = HttpServer();
        var refused = await Assert.ThrowsAsync<IOException>(
            () => McpToolSource.ConnectAsync(HttpServerAt(fake, "wrong-token-19"), [new("echo")], TestContext.Current.CancellationToken));
        Assert.Contains("401", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-token-19", refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EVT_03_a_server_s_credential_is_redacted_from_its_results_even_when_the_host_did_not_add_it_to_the_secrets()
    {
        var server = StdioServer(Token);
        await using var source = await McpToolSource.ConnectAsync(server, [new("token")], TestContext.Current.CancellationToken);
        var model = new ScriptedModel().CallTools(Call("c1", "fake__token", new { })).Reply("Done.");
        var agent = AgentOf(model, source, server) with { Secrets = [] };

        await agent.RunAsync(new Conversation(), "Go.", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new ToolResult("c1", "my token is [redacted]", false), LastResults(model)[0]);
    }

    [Fact]
    public async Task MCP_04_a_connect_cancelled_while_the_server_starts_leaves_no_server_running()
    {
        var processIdFile = Path.Combine(Path.GetTempPath(), $"s11-fake-mcp-{Guid.NewGuid():N}.pid");
        var silent = McpServer.Stdio("fake", "dotnet", [FakeServerProgram, "silent", processIdFile]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var connecting = McpToolSource.ConnectAsync(silent, [], cancellation.Token);

        // The server has started, and will never answer: the connect is cancelled while it waits for the answer.
        while (!File.Exists(processIdFile))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var processId = int.Parse(await File.ReadAllTextAsync(processIdFile, TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connecting);
        File.Delete(processIdFile);
        Assert.True(HasExited(processId), "The server process outlived the cancelled connect.");
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
