using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Shortener;

public sealed record Link(string Code, string Url, long Visits, string? LastVisitedAt, string CreatedAt);

public static partial class LinkRules
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public static bool IsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0
        && (url!.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static bool IsCode(string? code) => code is not null && Code().IsMatch(code);

    public static string NewCode() => RandomNumberGenerator.GetString(Alphabet, 7);

    [GeneratedRegex("^[A-Za-z0-9_-]{3,32}$")]
    private static partial Regex Code();
}

/// <summary>The links, in SQLite. A deleted link keeps its row, so its code is never used again.</summary>
public sealed class LinkStore(string path)
{
    private readonly string connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    public void Create() => Execute(
        "CREATE TABLE IF NOT EXISTS links (code TEXT PRIMARY KEY, url TEXT NOT NULL, visits INTEGER NOT NULL DEFAULT 0, last_visited_at TEXT, created_at TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0)");

    /// <summary>Adds the link; false when the code is taken, even by a deleted link.</summary>
    public bool TryAdd(string code, string url, out Link link)
    {
        link = new(code, url, 0, null, Now());
        try
        {
            Execute("INSERT INTO links (code, url, created_at) VALUES ($code, $url, $created)", ("$code", code), ("$url", url), ("$created", link.CreatedAt));
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public Link? Find(string code) => Query("WHERE code = $code AND deleted = 0", ("$code", code)).FirstOrDefault();

    public List<Link> All() => Query("WHERE deleted = 0 ORDER BY created_at DESC, rowid DESC");

    public bool Visit(string code) =>
        Execute("UPDATE links SET visits = visits + 1, last_visited_at = $now WHERE code = $code AND deleted = 0", ("$code", code), ("$now", Now())) > 0;

    public bool Delete(string code) => Execute("UPDATE links SET deleted = 1 WHERE code = $code AND deleted = 0", ("$code", code)) > 0;

    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private List<Link> Query(string where, params (string, object)[] parameters)
    {
        using var db = Open();
        var command = db.CreateCommand();
        command.CommandText = $"SELECT code, url, visits, last_visited_at, created_at FROM links {where}";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        var links = new List<Link>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            links.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        }

        return links;
    }

    private int Execute(string sql, params (string, object)[] parameters)
    {
        using var db = Open();
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connection);
        db.Open();
        return db;
    }
}
