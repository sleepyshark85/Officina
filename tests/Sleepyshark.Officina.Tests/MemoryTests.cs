using System.Text.Json;
using Sleepyshark.Officina.Memory.Files;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Tests;

/// <summary>The memory service and stores (MEM-01…05, AUD-03), through real runs: only the model and the approver are scripted.</summary>
public sealed class MemoryTests : IDisposable
{
    private readonly List<DirectoryInfo> folders = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly int[] ToEnd = [2, -1];

    private static readonly int[] BadRange = [3, 1];

    private static ToolCall Memory(string id, object input) => new(id, MemoryTool.Name, JsonSerializer.Serialize(input));

    /// <summary>Runs one reply's memory calls in <paramref name="scope"/>, and returns their results.</summary>
    private static async Task<IReadOnlyList<ToolResult>> RunAsync(IMemoryStore store, string scope, params object[] inputs)
    {
        var model = new ScriptedModel().CallTools([.. inputs.Select((input, index) => Memory($"m{index}", input))]).Reply("Done.");
        var agent = Agents.With(model, tools: [MemoryTool.Create(store)]);
        var result = await agent.RunAsync(new Conversation(), "Go.", null, scope, Ct);
        Assert.IsType<Completed>(result);
        return [.. model.Requests[^1].Messages[^1].Blocks.Select(block => block.ToolResult!)];
    }

    public static TheoryData<string> Stores => ["in memory", "files"];

    public void Dispose() => folders.ForEach(folder => folder.Delete(recursive: true));

    private IMemoryStore Store(string kind)
    {
        if (kind != "files")
        {
            return new InMemoryMemoryStore();
        }

        folders.Add(Directory.CreateTempSubdirectory("officina-memory-"));
        return new FileMemoryStore(folders[^1].FullName);
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task MEM_01_the_model_views_creates_edits_renames_and_deletes_files_with_the_memory_tool_s_commands(string kind)
    {
        var store = Store(kind);

        var results = await RunAsync(
            store,
            "sam",
            new { command = "view", path = "/memories" },
            new { command = "create", path = "/memories/prefs.md", file_text = "Prices: without tax.\nTone: brief.\n" },
            new { command = "str_replace", path = "/memories/prefs.md", old_str = "without", new_str = "with" },
            new { command = "insert", path = "/memories/prefs.md", insert_line = 0, insert_text = "# Sam\n" },
            new { command = "view", path = "/memories/prefs.md" },
            new { command = "view", path = "/memories/prefs.md", view_range = ToEnd },
            new { command = "create", path = "/memories/customers/ana/notes.md", file_text = "Likes crime." },
            new { command = "rename", old_path = "/memories/customers", new_path = "/memories/people" },
            new { command = "view", path = "/memories/" },
            new { command = "delete", path = "/memories/people" },
            new { command = "view", path = "/memories/people/ana/notes.md" });

        Assert.Equal(
            [
                "Here're the files and directories up to 2 levels deep in /memories, excluding hidden items and node_modules:\n0B\t/memories",
                "File created successfully at: /memories/prefs.md",
                "The memory file has been edited.",
                "The file /memories/prefs.md has been edited.",
                "Here's the content of /memories/prefs.md with line numbers:\n     1\t# Sam\n     2\tPrices: with tax.\n     3\tTone: brief.",
                "Here's the content of /memories/prefs.md with line numbers:\n     2\tPrices: with tax.\n     3\tTone: brief.",
                "File created successfully at: /memories/customers/ana/notes.md",
                "Successfully renamed /memories/customers to /memories/people",
                "Here're the files and directories up to 2 levels deep in /memories, excluding hidden items and node_modules:\n" +
                "49B\t/memories\n12B\t/memories/people\n12B\t/memories/people/ana\n37B\t/memories/prefs.md",
                "Successfully deleted /memories/people",
                "Error: The path /memories/people/ana/notes.md does not exist. Please provide a valid path.",
            ],
            results.Select(result => result.Content));
        Assert.Equal([.. Enumerable.Repeat(false, 10), true], results.Select(result => result.IsError));
        Assert.Equal([new MemoryFile("prefs.md", 37)], await store.ListAsync("sam", Ct));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task MEM_01_mistakes_are_error_results_that_change_nothing(string kind)
    {
        var store = Store(kind);
        await store.WriteAsync("sam", "a.md", "x\nx\n", Ct);
        await store.WriteAsync("sam", "b.md", "b", Ct);

        var results = await RunAsync(
            store,
            "sam",
            new { command = "str_replace", path = "/memories/a.md", old_str = "x", new_str = "y" },
            new { command = "str_replace", path = "/memories/a.md", old_str = "z", new_str = "y" },
            new { command = "insert", path = "/memories/a.md", insert_line = 9, insert_text = "y" },
            new { command = "create", path = "/memories/a.md/c.md", file_text = "y" },
            new { command = "create", path = "/memories" },
            new { command = "rename", old_path = "/memories/a.md", new_path = "/memories/b.md" },
            new { command = "delete", path = "/memories" },
            new { command = "view", path = "/memories/a.md", view_range = BadRange },
            new { command = "undo", path = "/memories/a.md" });

        Assert.All(results, result => Assert.True(result.IsError, result.Content));
        Assert.Equal("No replacement was performed. Multiple occurrences of old_str `x` in lines: 1, 2. Please ensure it is unique", results[0].Content);
        Assert.Equal("x\nx\n", await store.ReadAsync("sam", "a.md", Ct));
        Assert.Equal(["a.md", "b.md"], (await store.ListAsync("sam", Ct)).Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/memories/../secrets.env")]
    [InlineData("/memories/notes/../../x")]
    [InlineData("/memories/%2e%2e/x")]
    [InlineData("/memories/..\\x")]
    [InlineData("/memories//x")]
    [InlineData("/memoriesx/a")]
    [InlineData("memories/a")]
    [InlineData("/memories/C:/a")]
    public async Task MEM_03_a_path_outside_the_scope_is_refused_with_an_error_result(string path)
    {
        var store = new InMemoryMemoryStore();

        var results = await RunAsync(store, "sam", new { command = "create", path, file_text = "x" }, new { command = "rename", old_path = "/memories/a", new_path = path });

        Assert.All(results, result => Assert.StartsWith($"Error: The path {path} is outside the memory directory", result.Content, StringComparison.Ordinal));
        Assert.Empty(await store.ListAsync("sam", Ct));
    }

    [Fact]
    public async Task MEM_03_a_run_sees_only_its_scope_s_files()
    {
        var store = new InMemoryMemoryStore();
        await RunAsync(store, "ana", new { command = "create", path = "/memories/a.md", file_text = "Ana's" });

        var results = await RunAsync(store, "ben", new { command = "view", path = "/memories" }, new { command = "view", path = "/memories/a.md" });

        Assert.EndsWith("\n0B\t/memories", results[0].Content, StringComparison.Ordinal);
        Assert.True(results[1].IsError);
        Assert.Equal("Ana's", await store.ReadAsync("ana", "a.md", Ct));
    }

    [Fact]
    public void MEM_03_a_run_of_an_agent_with_memory_needs_a_valid_scope()
    {
        var agent = Agents.With(new ScriptedModel(), tools: [MemoryTool.Create(new InMemoryMemoryStore())]);

        Assert.Throws<ArgumentException>(() => agent.StreamAsync(new Conversation(), "Hi", cancellationToken: Ct));
        Assert.Throws<ArgumentException>(() => agent.StreamAsync(new Conversation(), "Hi", null, "../ana", Ct));
        Assert.Throws<ArgumentException>(() => agent.StreamAsync(new Conversation(), "Hi", null, "", Ct));
    }

    [Fact]
    public async Task MEM_04_memory_writes_are_audited_before_they_run_and_ask_approval_while_views_do_not()
    {
        var sink = new RecordingSink();
        var store = new AuditedStore(sink);
        var approver = new ScriptedApprover().Answer(Approval.Granted, Approval.Denied("not now"));
        var model = new ScriptedModel()
            .CallTools(
                Memory("v", new { command = "view", path = "/memories" }),
                Memory("c", new { command = "create", path = "/memories/a.md", file_text = "x" }),
                Memory("d", new { command = "delete", path = "/memories/a.md" }))
            .Reply("Done.");
        var agent = Agents.With(model, tools: [MemoryTool.Create(store, needsApproval: true)]) with { AuditSink = sink, Approver = approver };

        await agent.RunAsync(new Conversation(), "Go.", null, "sam", Ct);

        Assert.Equal(["c", "d"], approver.Asked.Select(call => call.Id));
        Assert.Equal(1, store.Writes);
        Assert.Equal("x", await store.ReadAsync("sam", "a.md", Ct));
        var ended = sink.Entries.Where(entry => entry.Kind == AuditKind.ToolEnded).ToList();
        Assert.Equal(["ok", "ok", "error"], ended.Select(entry => entry.Outcome));
        Assert.Equal("The call was denied: not now", ended[2].Detail);
    }

    [Fact]
    public async Task AUD_03_every_audit_entry_of_a_run_names_its_memory_scope()
    {
        var sink = new RecordingSink();
        var model = new ScriptedModel().CallTools(Memory("c", new { command = "create", path = "/memories/a.md", file_text = "x" })).Reply("Done.");
        var agent = Agents.With(model, tools: [MemoryTool.Create(new InMemoryMemoryStore())]) with { AuditSink = sink };

        await agent.RunAsync(new Conversation(), "Go.", null, "sam", Ct);

        Assert.Equal([AuditKind.RunStarted, AuditKind.ToolStarted, AuditKind.ToolEnded, AuditKind.RunEnded], sink.Entries.Select(entry => entry.Kind));
        Assert.All(sink.Entries, entry => Assert.Equal("sam", entry.MemoryScope));
    }

    [Fact]
    public async Task MEM_05_memory_never_reaches_the_instructions_and_the_prefix_stays_stable_as_it_changes()
    {
        var store = new InMemoryMemoryStore();
        await store.WriteAsync("sam", "prefs.md", "Prices with tax.", Ct);
        var model = new ScriptedModel()
            .CallTools(Memory("v", new { command = "view", path = "/memories/prefs.md" }))
            .CallTools(Memory("c", new { command = "create", path = "/memories/prefs.md", file_text = "Prices without tax." }))
            .Reply("Noted.")
            .Reply("Hello again.");
        var agent = Agents.With(model, tools: [MemoryTool.Create(store)]);
        var conversation = new Conversation();

        await agent.RunAsync(conversation, "What do I prefer?", null, "sam", Ct);
        await agent.RunAsync(conversation, "Hi.", null, "sam", Ct);

        Assert.All(model.Requests, request => Assert.Equal(Agents.Instructions, request.Instructions));
        Assert.Empty(PrefixStability.Problems(model.Requests));
        Assert.Contains("Prices with tax.", model.Requests[1].Messages[^1].Blocks[0].ToolResult!.Content, StringComparison.Ordinal);
        Assert.True(agent.CanContinue(conversation));
    }

    /// <summary>A store that checks, at each write, that the call's attempt is already in the audit trail (AUD-02).</summary>
    private sealed class AuditedStore(RecordingSink sink) : IMemoryStore
    {
        private readonly InMemoryMemoryStore inner = new();

        public int Writes { get; private set; }

        public Task<IReadOnlyList<MemoryFile>> ListAsync(string scope, CancellationToken cancellationToken) => inner.ListAsync(scope, cancellationToken);

        public Task<string?> ReadAsync(string scope, string path, CancellationToken cancellationToken) => inner.ReadAsync(scope, path, cancellationToken);

        public Task WriteAsync(string scope, string path, string content, CancellationToken cancellationToken)
        {
            Assert.Equal(AuditKind.ToolStarted, sink.Entries[^1].Kind);
            Writes++;
            return inner.WriteAsync(scope, path, content, cancellationToken);
        }

        public Task DeleteAsync(string scope, string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Not approved.");

        public Task RenameAsync(string scope, string path, string newPath, CancellationToken cancellationToken) => throw new InvalidOperationException("Not approved.");
    }
}
