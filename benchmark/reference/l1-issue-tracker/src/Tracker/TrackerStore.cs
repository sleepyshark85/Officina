using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tracker.Domain;

namespace Tracker;

/// <summary>The tracker's data, in SQLite.</summary>
public sealed class TrackerStore(string path)
{
    private readonly string connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    public void Create() => Execute("""
        CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE COLLATE NOCASE, admin INTEGER NOT NULL, token TEXT NOT NULL UNIQUE);
        CREATE TABLE IF NOT EXISTS projects (key TEXT PRIMARY KEY, name TEXT NOT NULL, creator_id INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS members (project_key TEXT NOT NULL, user_id INTEGER NOT NULL, PRIMARY KEY (project_key, user_id));
        CREATE TABLE IF NOT EXISTS issues (id TEXT PRIMARY KEY, project_key TEXT NOT NULL, number INTEGER NOT NULL, title TEXT NOT NULL, description TEXT NOT NULL,
            type TEXT NOT NULL, priority TEXT NOT NULL, assignee_id INTEGER, labels TEXT NOT NULL, status TEXT NOT NULL, reporter_id INTEGER NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS comments (id INTEGER PRIMARY KEY AUTOINCREMENT, issue_id TEXT NOT NULL, author_id INTEGER NOT NULL, body TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS history (seq INTEGER PRIMARY KEY AUTOINCREMENT, issue_id TEXT NOT NULL, user_id INTEGER NOT NULL, at TEXT NOT NULL, field TEXT NOT NULL, old TEXT NOT NULL, new TEXT NOT NULL);
        """);

    /// <summary>Adds a user, an admin when it is the first; null when the email is taken.</summary>
    public (User User, string Token)? AddUser(string name, string email)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        try
        {
            var id = Scalar("INSERT INTO users (name, email, admin, token) VALUES ($a, $b, NOT EXISTS (SELECT 1 FROM users), $c) RETURNING id", name, email, token);
            return (User(id)!, token);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return null;
        }
    }

    public User? User(long id) => Users("WHERE id = $a", id).FirstOrDefault();

    public User? UserByToken(string token) => Users("WHERE token = $a", token).FirstOrDefault();

    public bool AddProject(Project project)
    {
        try
        {
            Execute("INSERT INTO projects (key, name, creator_id) VALUES ($a, $b, $c); INSERT INTO members (project_key, user_id) VALUES ($a, $c)",
                project.Key, project.Name, project.CreatorId);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public List<Project> Projects() =>
        Query("SELECT key, name, creator_id FROM projects ORDER BY key", r => new Project(r.GetString(0), r.GetString(1), r.GetInt64(2)));

    public Project? Project(string key) => Projects().FirstOrDefault(project => project.Key == key);

    public bool IsMember(string key, long userId) => Scalar("SELECT COUNT(*) FROM members WHERE project_key = $a AND user_id = $b", key, userId) > 0;

    public void AddMember(string key, long userId) => Execute("INSERT OR IGNORE INTO members (project_key, user_id) VALUES ($a, $b)", key, userId);

    public Issue AddIssue(string key, IssueInput input, long reporterId)
    {
        var now = Now();
        var number = (int)Scalar("SELECT COALESCE(MAX(number), 0) + 1 FROM issues WHERE project_key = $a", key);
        var issue = new Issue($"{key}-{number}", key, number, input.Title!, input.Description!, input.Type!, input.Priority!, input.AssigneeId, input.Labels!, "open", reporterId, now, now);
        Save(issue, insert: true);
        return issue;
    }

    public List<Issue> Issues(string key) => IssuesWhere("WHERE project_key = $a", key);

    public Issue? Issue(string id) => IssuesWhere("WHERE id = $a", id).FirstOrDefault();

    /// <summary>Saves the issue's new fields and keeps each change in its history.</summary>
    public Issue Change(Issue old, Issue changed, long userId)
    {
        var at = Now();
        changed = changed with { UpdatedAt = at };
        Save(changed, insert: false);
        foreach (var (field, before, after) in Differences(old, changed))
        {
            Execute("INSERT INTO history (issue_id, user_id, at, field, old, new) VALUES ($a, $b, $c, $d, $e, $f)", old.Id, userId, at, field, before, after);
        }

        return changed;
    }

    public List<Change> History(string issueId) => Query(
        "SELECT user_id, at, field, old, new FROM history WHERE issue_id = $a ORDER BY seq",
        r => new Change(r.GetInt64(0), r.GetString(1), r.GetString(2), JsonDocument.Parse(r.GetString(3)).RootElement.Clone(), JsonDocument.Parse(r.GetString(4)).RootElement.Clone()),
        issueId);

    public Comment AddComment(string issueId, long authorId, string body)
    {
        var now = Now();
        return new(Scalar("INSERT INTO comments (issue_id, author_id, body, created_at, updated_at) VALUES ($a, $b, $c, $d, $d) RETURNING id", issueId, authorId, body, now), issueId, authorId, body, now, now);
    }

    public List<Comment> Comments(string issueId) => Query(
        "SELECT id, issue_id, author_id, body, created_at, updated_at FROM comments WHERE issue_id = $a ORDER BY id",
        r => new Comment(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5)), issueId);

    public Comment SaveComment(Comment comment)
    {
        comment = comment with { UpdatedAt = Now() };
        Execute("UPDATE comments SET body = $a, updated_at = $b WHERE id = $c", comment.Body, comment.UpdatedAt, comment.Id);
        return comment;
    }

    private static IEnumerable<(string Field, string Old, string New)> Differences(Issue old, Issue changed)
    {
        (string, object?, object?)[] fields =
        [
            ("title", old.Title, changed.Title), ("description", old.Description, changed.Description), ("type", old.Type, changed.Type),
            ("priority", old.Priority, changed.Priority), ("assigneeId", old.AssigneeId, changed.AssigneeId), ("labels", old.Labels, changed.Labels),
            ("status", old.Status, changed.Status),
        ];
        foreach (var (field, before, after) in fields)
        {
            var (oldJson, newJson) = (JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
            if (oldJson != newJson)
            {
                yield return (field, oldJson, newJson);
            }
        }
    }

    private void Save(Issue issue, bool insert) => Execute(
        insert
            ? "INSERT INTO issues (id, project_key, number, title, description, type, priority, assignee_id, labels, status, reporter_id, created_at, updated_at) VALUES ($a, $b, $c, $d, $e, $f, $g, $h, $i, $j, $k, $l, $m)"
            : "UPDATE issues SET title = $d, description = $e, type = $f, priority = $g, assignee_id = $h, labels = $i, status = $j, updated_at = $m WHERE id = $a AND project_key = $b AND number = $c AND reporter_id = $k AND created_at = $l",
        issue.Id, issue.ProjectKey, issue.Number, issue.Title, issue.Description, issue.Type, issue.Priority, (object?)issue.AssigneeId ?? DBNull.Value,
        JsonSerializer.Serialize(issue.Labels), issue.Status, issue.ReporterId, issue.CreatedAt, issue.UpdatedAt);

    private List<Issue> IssuesWhere(string where, params object[] values) => Query(
        $"SELECT id, project_key, number, title, description, type, priority, assignee_id, labels, status, reporter_id, created_at, updated_at FROM issues {where} ORDER BY number",
        r => new Issue(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetInt64(7),
            JsonSerializer.Deserialize<string[]>(r.GetString(8))!, r.GetString(9), r.GetInt64(10), r.GetString(11), r.GetString(12)),
        values);

    private List<User> Users(string where, params object[] values) =>
        Query($"SELECT id, name, email, admin FROM users {where}", r => new User(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3) == 1), values);

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params object[] values)
    {
        using var db = Open();
        using var command = Command(db, sql, values);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private long Scalar(string sql, params object[] values)
    {
        using var db = Open();
        using var command = Command(db, sql, values);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql, params object[] values)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = Command(db, sql, values);
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, object[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        for (var i = 0; i < values.Length; i++)
        {
            command.Parameters.AddWithValue("$" + (char)('a' + i), values[i]);
        }

        return command;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connection);
        db.Open();
        return db;
    }
}
