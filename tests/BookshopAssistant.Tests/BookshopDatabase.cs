using System.Runtime.CompilerServices;
using Npgsql;
using Testcontainers.PostgreSql;

namespace BookshopAssistant.Tests;

/// <summary>
/// A fresh copy of the seeded bookshop database per test class. One PostgreSQL container, started on first use with the
/// app's schema and seed, serves the run; each class gets its own clone, so classes never see each other's changes.
/// Linux only, and only where Docker is available.
/// </summary>
public sealed class BookshopDatabase : IAsyncLifetime
{
    private const string Seeded = "bookshop";

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PostgreSqlContainer? container;

    private string name = "";

    /// <summary>The compose file's password: distinct from the user and database names.</summary>
    public const string Password = "shelf-demo-41";

    /// <summary>
    /// Whether the database tests run here: only on Linux; in CI always (where missing Docker fails them rather than
    /// skipping silently), elsewhere on Linux when Docker is found.
    /// </summary>
    public static bool Available =>
        OperatingSystem.IsLinux()
        && (Environment.GetEnvironmentVariable("CI") is not null
            || File.Exists("/var/run/docker.sock")
            || Environment.GetEnvironmentVariable("DOCKER_HOST") is not null);

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>The application's tools on this database.</summary>
    public BookshopTools Tools => new(DataSource);

    public async ValueTask InitializeAsync()
    {
        if (!Available)
        {
            return;
        }

        name = $"test_{Guid.NewGuid():N}";
        await Gate.WaitAsync();
        try
        {
            container ??= await StartContainerAsync();

            // Cloning needs no other session on the seeded database, so clones are made one at a time.
            await AdminAsync($"create database {name} template {Seeded}");
        }
        finally
        {
            Gate.Release();
        }

        DataSource = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }
    }

    /// <summary>
    /// Makes the database unreachable, as if its server stopped: new connections are refused and open ones ended.
    /// <see cref="BringBackAsync"/> undoes it.
    /// </summary>
    public async Task TakeDownAsync()
    {
        await AdminAsync($"alter database {name} allow_connections false");
        await AdminAsync($"select pg_terminate_backend(pid) from pg_stat_activity where datname = '{name}'");
    }

    /// <summary>Makes the database reachable again after <see cref="TakeDownAsync"/>.</summary>
    public async Task BringBackAsync()
    {
        await AdminAsync($"alter database {name} allow_connections true");
    }

    /// <summary>Runs a query of the test's own and returns its first value.</summary>
    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters)
    {
        await using var command = DataSource.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a statement on the server's maintenance database, as administrator.</summary>
    private static async Task AdminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(container!.GetConnectionString()) { Database = "postgres", Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgreSqlContainer> StartContainerAsync()
    {
        var started = new PostgreSqlBuilder("postgres:17")
            .WithDatabase(Seeded)
            .WithUsername("bookshop")
            .WithPassword(Password)
            .WithResourceMapping(new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "database")), "/docker-entrypoint-initdb.d/")
            .Build();
        await started.StartAsync();
        return started;
    }
}

/// <summary>A test against the database in Docker: skipped where <see cref="BookshopDatabase.Available"/> is false.</summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public DatabaseFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Needs Linux and Docker.";
        SkipUnless = nameof(BookshopDatabase.Available);
        SkipType = typeof(BookshopDatabase);
    }
}
