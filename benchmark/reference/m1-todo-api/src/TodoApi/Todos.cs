using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace TodoApi;

public sealed record Todo(long Id, string Title, bool Done, string? Due, string CreatedAt);

/// <summary>The fields of a request body that are valid, and the errors of those that are not, by field.</summary>
public sealed class TodoInput
{
    public Dictionary<string, string[]> Errors { get; } = [];

    public string? Title { get; private set; }

    public bool? Done { get; private set; }

    public bool HasDue { get; private set; }

    public string? Due { get; private set; }

    public static TodoInput Read(JsonElement body, bool creating)
    {
        var input = new TodoInput();
        if (body.ValueKind != JsonValueKind.Object)
        {
            input.Errors["body"] = ["The body must be a JSON object."];
            return input;
        }

        if (body.TryGetProperty("title", out var title))
        {
            var text = title.ValueKind == JsonValueKind.String ? title.GetString()!.Trim() : null;
            if (text is null or { Length: 0 or > 200 })
            {
                input.Errors["title"] = ["The title must have 1 to 200 characters."];
            }

            input.Title = text;
        }
        else if (creating)
        {
            input.Errors["title"] = ["The title is required."];
        }

        if (body.TryGetProperty("done", out var done))
        {
            if (done.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                input.Done = done.GetBoolean();
            }
            else
            {
                input.Errors["done"] = ["Done must be true or false."];
            }
        }

        if (body.TryGetProperty("due", out var due))
        {
            input.HasDue = true;
            if (due.ValueKind == JsonValueKind.Null)
            {
                input.Due = null;
            }
            else if (due.ValueKind == JsonValueKind.String && IsDate(due.GetString()!))
            {
                input.Due = due.GetString();
            }
            else
            {
                input.Errors["due"] = ["The due date must be yyyy-MM-dd."];
            }
        }

        return input;
    }

    public static bool IsDate(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

/// <summary>The to-dos, in SQLite.</summary>
public sealed class TodoStore(string path)
{
    private readonly string connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    public void Create()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE IF NOT EXISTS todos (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL, done INTEGER NOT NULL, due TEXT, created_at TEXT NOT NULL)");
    }

    public Todo Add(string title, string? due, DateTimeOffset now)
    {
        using var db = Open();
        var command = db.CreateCommand();
        command.CommandText = "INSERT INTO todos (title, done, due, created_at) VALUES ($title, 0, $due, $created) RETURNING id";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$due", (object?)due ?? DBNull.Value);
        var created = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        command.Parameters.AddWithValue("$created", created);
        return new((long)command.ExecuteScalar()!, title, false, due, created);
    }

    public Todo? Find(long id) => Query("WHERE id = $id", [("$id", id)], 1, 0).Items.FirstOrDefault();

    public void Save(Todo todo)
    {
        using var db = Open();
        var command = db.CreateCommand();
        command.CommandText = "UPDATE todos SET title = $title, done = $done, due = $due WHERE id = $id";
        command.Parameters.AddWithValue("$title", todo.Title);
        command.Parameters.AddWithValue("$done", todo.Done ? 1 : 0);
        command.Parameters.AddWithValue("$due", (object?)todo.Due ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", todo.Id);
        command.ExecuteNonQuery();
    }

    public bool Delete(long id)
    {
        using var db = Open();
        var command = db.CreateCommand();
        command.CommandText = "DELETE FROM todos WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public (List<Todo> Items, long Total) List(bool? done, string? dueBefore, int page, int pageSize)
    {
        var conditions = new List<string>();
        var parameters = new List<(string, object)>();
        if (done is { } wanted)
        {
            conditions.Add("done = $done");
            parameters.Add(("$done", wanted ? 1 : 0));
        }

        if (dueBefore is not null)
        {
            conditions.Add("due IS NOT NULL AND due < $before");
            parameters.Add(("$before", dueBefore));
        }

        return Query(conditions.Count == 0 ? "" : "WHERE " + string.Join(" AND ", conditions), parameters, pageSize, (page - 1) * pageSize);
    }

    private (List<Todo> Items, long Total) Query(string where, List<(string, object)> parameters, int limit, int offset)
    {
        using var db = Open();
        var count = db.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM todos {where}";
        var select = db.CreateCommand();
        select.CommandText = $"SELECT id, title, done, due, created_at FROM todos {where} ORDER BY id LIMIT {limit} OFFSET {offset}";
        foreach (var (name, value) in parameters)
        {
            count.Parameters.AddWithValue(name, value);
            select.Parameters.AddWithValue(name, value);
        }

        var items = new List<Todo>();
        using var reader = select.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) == 1, reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
        }

        return (items, (long)count.ExecuteScalar()!);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connection);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
