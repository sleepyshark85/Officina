using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Storage.Sqlite.Tests;

/// <summary>The SQLite storage, in a real file in a temporary folder (DESIGN.md §11).</summary>
public sealed class SqliteStorageTests : StorageContract, IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("officina-").FullName;

    private string File => Path.Combine(folder, "officina.db");

    private string Artifacts => Path.Combine(folder, "artifacts");

    public void Dispose() => Directory.Delete(folder, recursive: true);

    protected override async Task<IStorage> CreateAsync() => await SqliteStorage.OpenAsync(File, Ct);

    [Fact]
    public async Task Stored_data_survives_reopening_the_file()
    {
        await (await CreateAsync()).Events.AppendAsync("acme", Event("run-1", 1), Ct);

        Assert.Equal([Event("run-1", 1)], await (await CreateAsync()).Events.ReadAsync("acme", "run-1", 0, Ct));
    }

    // REL-04: a version-3 file has no memory table, so it is refused rather than failing at the first change.
    [Fact]
    public async Task A_file_in_another_format_version_is_refused()
    {
        await CreateAsync();
        await using (var connection = new SqliteConnection($"Data Source={File};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 3";
            await command.ExecuteNonQueryAsync(Ct);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CreateAsync());

        Assert.Equal(
            $"{File} holds data in format version 3, and this core reads format version 6 only. Move it aside to start with empty storage, or use the version that wrote it.",
            error.Message);
    }

    // STO-01: the bytes are a file named by the artifact's id, never by its name, and the row holds the rest.
    [Fact]
    public async Task An_artifact_is_a_file_named_by_its_id_in_the_folder_beside_the_database()
    {
        var storage = await CreateAsync();

        var id = await storage.Artifacts.SaveAsync("acme", "run-1", new("../../escape.md", "# Ünïcødé"), Start, Ct);

        Assert.Equal([Path.Combine(Artifacts, $"{id}")], Directory.GetFiles(Artifacts));
        Assert.Equal("# Ünïcødé", await System.IO.File.ReadAllTextAsync(Path.Combine(Artifacts, $"{id}"), Ct));
        Assert.False(System.IO.File.Exists(Path.GetFullPath(Path.Combine(Artifacts, "../../escape.md"))));
        var (name, sha256) = Assert.Single(await QueryAsync("SELECT name, sha256 FROM artifacts"));
        Assert.Equal("../../escape.md", name);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("# Ünïcødé"u8)), sha256);
    }

    // STO-01: a file without a row at the id an artifact gets, such as one a failed commit left after the storage opened, is an
    // orphan; the save replaces it and succeeds, so one orphan cannot make later saves fail.
    [Fact]
    public async Task An_orphan_file_at_the_next_id_is_replaced_and_the_save_succeeds()
    {
        var storage = await CreateAsync();
        Directory.CreateDirectory(Artifacts);
        await System.IO.File.WriteAllTextAsync(Path.Combine(Artifacts, "1"), "an orphan", Ct);

        var id = await storage.Artifacts.SaveAsync("acme", "run-1", new("report.md", "text"), Start, Ct);

        Assert.Equal(1, id);
        Assert.Equal("text", await System.IO.File.ReadAllTextAsync(Path.Combine(Artifacts, "1"), Ct));
        Assert.Equal(new Artifact("report.md", "text"), await storage.Artifacts.ReadAsync("acme", "run-1", id, Ct));
    }

    // STO-01, REL-04: the file is written before its row is committed, so a failed write leaves no row behind.
    [Fact]
    public async Task An_artifact_whose_file_cannot_be_written_leaves_no_row()
    {
        var storage = await CreateAsync();
        await System.IO.File.WriteAllTextAsync(Artifacts, "a file where the folder should be", Ct);

        await Assert.ThrowsAnyAsync<IOException>(async () => await storage.Artifacts.SaveAsync("acme", "run-1", new("report.md", "text"), Start, Ct));

        Assert.Empty(await QueryAsync("SELECT name, sha256 FROM artifacts"));
        System.IO.File.Delete(Artifacts);
        var id = await storage.Artifacts.SaveAsync("acme", "run-1", new("report.md", "text"), Start, Ct);
        Assert.Equal(new Artifact("report.md", "text"), await storage.Artifacts.ReadAsync("acme", "run-1", id, Ct));
    }

    // STO-01, RUN-04: a crash between the file and the row leaves a file without a row, or a temporary one; the next open removes
    // them, leaves files it did not name alone, and the artifacts saved before are read as they were.
    [Fact]
    public async Task Files_a_crash_left_without_a_row_are_removed_when_the_storage_opens_again()
    {
        var id = await (await CreateAsync()).Artifacts.SaveAsync("acme", "run-1", new("report.md", "kept"), Start, Ct);
        await System.IO.File.WriteAllTextAsync(Path.Combine(Artifacts, $"{id + 1}"), "no row", Ct);
        await System.IO.File.WriteAllTextAsync(Path.Combine(Artifacts, $"{id + 2}.tmp"), "half", Ct);
        await System.IO.File.WriteAllTextAsync(Path.Combine(Artifacts, "notes.txt"), "the owner's", Ct);

        var storage = await CreateAsync();

        Assert.Equal([$"{id}", "notes.txt"], Directory.GetFiles(Artifacts).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(new Artifact("report.md", "kept"), await storage.Artifacts.ReadAsync("acme", "run-1", id, Ct));
        var next = await storage.Artifacts.SaveAsync("acme", "run-1", new("next.md", "new"), Start, Ct);
        Assert.Equal(new Artifact("next.md", "new"), await storage.Artifacts.ReadAsync("acme", "run-1", next, Ct));
    }

    // STO-01: a file that is not what was saved, or is gone while its row is there, is reported rather than misread.
    [Fact]
    public async Task A_damaged_or_missing_artifact_file_is_reported()
    {
        var storage = await CreateAsync();
        var id = await storage.Artifacts.SaveAsync("acme", "run-1", new("report.md", "the text"), Start, Ct);
        var path = Path.Combine(Artifacts, $"{id}");

        await System.IO.File.WriteAllTextAsync(path, "the t3xt", Ct);
        var damaged = await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.Artifacts.ReadAsync("acme", "run-1", id, Ct));
        System.IO.File.Delete(path);
        var missing = await Assert.ThrowsAsync<InvalidDataException>(async () => await storage.Artifacts.ReadAsync("acme", "run-1", id, Ct));

        Assert.Equal($"The file of artifact {id}, {path}, is damaged: it is not what was saved.", damaged.Message);
        Assert.Equal($"The file of artifact {id}, {path}, is missing.", missing.Message);
    }

    // PRIV-02, STO-01: deleting an owner's data deletes their artifacts' files; other owners' stay.
    [Fact]
    public async Task Deleting_an_owners_data_deletes_their_artifact_files()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");
        await StoreRunAsync(storage, "acme", "run-2", "bob");
        Assert.Equal(2, Directory.GetFiles(Artifacts).Length);

        await storage.DeleteAsync("acme", "ann", Ct);

        Assert.Equal([new Artifact("run-2 result", "full text")], (await storage.ExportAsync("acme", "bob", Ct)).Artifacts);
        Assert.Single(Directory.GetFiles(Artifacts));
    }

    // PRIV-01, STO-01: an expired artifact's file goes with its row.
    [Fact]
    public async Task An_expired_artifacts_file_is_deleted()
    {
        var storage = await CreateAsync();
        var old = await storage.Artifacts.SaveAsync("acme", "run-1", new("old.md", "old"), Start, Ct);
        var kept = await storage.Artifacts.SaveAsync("acme", "run-1", new("new.md", "new"), Start.AddDays(20), Ct);

        await storage.DeleteExpiredAsync(new RetentionOptions { Artifacts = TimeSpan.FromDays(10) }, Start.AddDays(25), Ct);

        Assert.Equal([Path.Combine(Artifacts, $"{kept}")], Directory.GetFiles(Artifacts));
        Assert.Null(await storage.Artifacts.ReadAsync("acme", "run-1", old, Ct));
        Assert.Equal(new Artifact("new.md", "new"), await storage.Artifacts.ReadAsync("acme", "run-1", kept, Ct));
    }

    // CFG-07, STO-01.
    [Fact]
    public async Task A_run_stores_its_configuration_as_text_and_its_events()
    {
        var options = new OfficinaOptions { Agents = new Dictionary<string, AgentDefinition> { ["extractor"] = new() { Instructions = "Extract." } } };
        var runner = new AgentRunner(
            options, new Dictionary<string, IModelProvider> { ["claude"] = new ScriptedModelProvider().Reply("A-17") }, await CreateAsync(),
            new Dictionary<string, ITool>(), new Dictionary<string, IGate>(), new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(),
            new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider());

        await runner.RunAsync("extractor", "Invoice A-17", ct: Ct);

        var (runId, configuration) = Assert.Single(await QueryAsync("SELECT run_id, configuration FROM runs"));
        Assert.Equal(JsonSerializer.Serialize(options, ConfigurationJson.Options), configuration);
        Assert.Equal(
            ["turnStarted", "modelCallEnded", "turnEnded"],
            (await (await CreateAsync()).Events.ReadAsync(null, runId, 0, Ct)).Select(read => read.Payload.Kind));
    }

    // CAP-05, CTX-06: a new runner on the same file stands in for a restarted process.
    [Fact]
    public async Task A_conversation_continues_with_its_full_history_after_a_restart()
    {
        var options = new OfficinaOptions
        {
            Agents = new Dictionary<string, AgentDefinition>
            {
                ["assistant"] = new() { Instructions = "Help.", Context = new() { History = new() { Strategy = HistoryStrategy.Full } } },
            },
            Capabilities = new() { ConversationStore = new() { Enabled = true } },
        };
        var before = new ScriptedModelProvider().Reply(new ContentReceived(new ReasoningContent("The user greets.", "sig")), new TextDelta("Hello."), new Stopped(StopReason.Finished));
        await Runner(options, before, await CreateAsync()).RunAsync("assistant", "Hi.", ct: Ct);

        var after = new ScriptedModelProvider().Reply("Fine.");
        await Runner(options, after, await CreateAsync()).RunAsync("assistant", "How are you?", ct: Ct);

        Assert.Equal(
            [Message.User("Hi."), new(Role.Assistant, [new ReasoningContent("The user greets.", "sig"), new TextContent("Hello.")]), Message.User("How are you?")],
            Assert.Single(after.Requests).History);
    }

    private static AgentRunner Runner(OfficinaOptions options, ScriptedModelProvider model, IStorage storage) => new(
        options, new Dictionary<string, IModelProvider> { ["claude"] = model }, storage, new Dictionary<string, ITool>(), new Dictionary<string, IGate>(),
        new Dictionary<string, ICheck>(), new Dictionary<string, IKnowledgeSource>(), new ScriptedHuman(), new InMemorySecretSource(new Dictionary<string, string>()), new FakeTimeProvider());

    private async Task<List<(string, string)>> QueryAsync(string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={File};Pooling=False");
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }
}
