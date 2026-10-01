using System.Text.Json;
using Sleepyshark.Officina.Core;
using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Events;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;
using Sleepyshark.Officina.Core.Running;
using Sleepyshark.Officina.Testing;

namespace Sleepyshark.Officina.Storage.Sqlite.Tests;

/// <summary>What every storage does (STO-01). The in-memory and the SQLite storage both pass it (DESIGN.md §11).</summary>
public abstract class StorageContract
{
    protected static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected abstract Task<IStorage> CreateAsync();

    [Fact]
    public async Task A_run_is_stored_with_the_configuration_it_used()
    {
        var storage = await CreateAsync();
        var configuration = new OfficinaOptions { Project = new() { Name = "invoice-api" } };
        await storage.Runs.RecordStartAsync("acme", Run("run-1", "ann") with { Configuration = configuration }, Ct);

        var run = Assert.Single((await storage.ExportAsync("acme", "ann", Ct)).Runs);

        Assert.Equal(("run-1", "extractor", "ann", Start, CoreVersion.Value), (run.RunId, run.Agent, run.Owner, run.Time, run.CoreVersion));
        Assert.Equal(Json(configuration), Json(run.Configuration));
    }

    // EVT-03.
    [Fact]
    public async Task Events_are_read_in_order_after_any_sequence_number()
    {
        var storage = await CreateAsync();
        foreach (var sequence in new[] { 1, 2, 5 })
        {
            await storage.Events.AppendAsync("acme", Event("run-1", sequence), Ct);
        }

        await storage.Events.AppendAsync("acme", Event("run-2", 3), Ct);

        Assert.Equal([1L, 2, 5], (await storage.Events.ReadAsync("acme", "run-1", 0, Ct)).Select(read => read.Sequence));
        Assert.Equal([Event("run-1", 5)], await storage.Events.ReadAsync("acme", "run-1", 2, Ct));
    }

    [Fact]
    public async Task Audit_entries_are_read_in_the_order_appended()
    {
        var storage = await CreateAsync();
        await storage.Audit.AppendAsync("acme", Entry("run-1", AuditOutcome.Intent), Ct);
        await storage.Audit.AppendAsync("acme", Entry("run-1", AuditOutcome.Failed) with { Detail = "IOException: disk full" }, Ct);

        Assert.Equal(
            [Entry("run-1", AuditOutcome.Intent), Entry("run-1", AuditOutcome.Failed) with { Detail = "IOException: disk full" }],
            await storage.Audit.ReadAsync("acme", "run-1", Ct));
    }

    // SEC-02, TEST-18.
    [Fact]
    public async Task A_tenant_cannot_see_another_tenants_data()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");

        foreach (var other in new[] { "globex", null })
        {
            Assert.Empty(await storage.Events.ReadAsync(other, "run-1", 0, Ct));
            Assert.Empty(await storage.Audit.ReadAsync(other, "run-1", Ct));
            Assert.Equal(0, await CountAsync(storage, other, "ann"));
            await storage.DeleteAsync(other, "ann", Ct);
        }

        Assert.Single((await storage.ExportAsync("acme", "ann", Ct)).Runs);
    }

    // PRIV-02, TEST-18.
    [Fact]
    public async Task An_owners_data_is_exported_on_request()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");
        await StoreRunAsync(storage, "acme", "run-2", "bob");

        var export = await storage.ExportAsync("acme", "ann", Ct);

        Assert.Equal(["run-1"], export.Runs.Select(run => run.RunId));
        Assert.Equal([Event("run-1", 1)], export.Events);
        Assert.Equal([Entry("run-1", AuditOutcome.Intent)], export.Audit);
    }

    // PRIV-02, TEST-18.
    [Fact]
    public async Task Deleting_an_owners_data_leaves_audit_entries_to_their_retention()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "run-1", "ann");
        await StoreRunAsync(storage, "acme", "run-2", "bob");

        await storage.DeleteAsync("acme", "ann", Ct);

        Assert.Equal(0, await CountAsync(storage, "acme", "ann"));
        Assert.Empty(await storage.Events.ReadAsync("acme", "run-1", 0, Ct));
        Assert.Equal([Entry("run-1", AuditOutcome.Intent)], await storage.Audit.ReadAsync("acme", "run-1", Ct));
        Assert.Single((await storage.ExportAsync("acme", "bob", Ct)).Runs);
    }

    // PRIV-01, EVT-05, TEST-18.
    [Fact]
    public async Task Data_older_than_its_kinds_retention_is_deleted()
    {
        var storage = await CreateAsync();
        await StoreRunAsync(storage, "acme", "old", "ann");
        await StoreRunAsync(storage, "acme", "new", "ann", Start.AddDays(20));

        await storage.DeleteExpiredAsync(new RetentionOptions { Events = TimeSpan.FromDays(10), Audit = TimeSpan.FromDays(30) }, Start.AddDays(25), Ct);

        var export = await storage.ExportAsync("acme", "ann", Ct);
        Assert.Equal(["old", "new"], export.Runs.Select(run => run.RunId));
        Assert.Equal(["new"], export.Events.Select(kept => kept.RunId));
        Assert.Equal(["old", "new"], export.Audit.Select(entry => entry.RunId));
    }

    protected static RunStarted Run(string runId, string? owner, DateTimeOffset? time = null) =>
        new(runId, "extractor", owner, time ?? Start, CoreVersion.Value, new OfficinaOptions());

    protected static CoreEvent Event(string runId, long sequence, DateTimeOffset? time = null) =>
        new(runId, "extractor", null, sequence, time ?? Start, new ModelCallEnded(StopReason.Finished, new Usage(10, 2, 0, 0), 0.5m));

    protected static AuditEntry Entry(string runId, AuditOutcome outcome, DateTimeOffset? time = null) =>
        new(runId, "extractor", "ann", "create_issue", """{"title":"Crash"}""", null, outcome, time ?? Start, "key");

    /// <summary>A run with one event and one audit entry, all at <paramref name="time"/>.</summary>
    private static async Task StoreRunAsync(IStorage storage, string tenant, string runId, string owner, DateTimeOffset? time = null)
    {
        await storage.Runs.RecordStartAsync(tenant, Run(runId, owner, time), Ct);
        await storage.Events.AppendAsync(tenant, Event(runId, 1, time), Ct);
        await storage.Audit.AppendAsync(tenant, Entry(runId, AuditOutcome.Intent, time), Ct);
    }

    /// <summary>How many runs, events and audit entries an export of the owner's data holds.</summary>
    private static async Task<int> CountAsync(IStorage storage, string? tenant, string owner)
    {
        var data = await storage.ExportAsync(tenant, owner, Ct);
        return data.Runs.Count + data.Events.Count + data.Audit.Count;
    }

    private static string Json(OfficinaOptions options) => JsonSerializer.Serialize(options, ConfigurationJson.Options);
}

public sealed class InMemoryStorageTests : StorageContract
{
    protected override Task<IStorage> CreateAsync() => Task.FromResult<IStorage>(new InMemoryStorage());
}
