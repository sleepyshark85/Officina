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

        Assert.Equal($"{File} holds data in format version 3, and this core reads format version 5 only.", error.Message);
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
