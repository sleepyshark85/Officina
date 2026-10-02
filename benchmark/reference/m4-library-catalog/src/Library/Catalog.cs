using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Library;

public sealed record Author(long Id, string Name);

public sealed record Book(long Id, string Isbn, string Title, int Year, long[] AuthorIds, int Copies);

public sealed record Member(long Id, string Name, string Email);

public sealed record Loan(long Id, long BookId, long MemberId, string LoanedOn, string DueOn, string? ReturnedOn);

public static partial class Rules
{
    public static bool IsIsbn13(string? isbn)
    {
        if (isbn is null || !Digits13().IsMatch(isbn))
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            sum += (isbn[i] - '0') * (i % 2 == 0 ? 1 : 3);
        }

        return (10 - (sum % 10)) % 10 == isbn[12] - '0';
    }

    public static bool IsEmail(string? email) => email is not null && Email().IsMatch(email);

    [GeneratedRegex("^[0-9]{13}$")]
    private static partial Regex Digits13();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();
}

/// <summary>Reads a request body's fields, collecting an error for each invalid one.</summary>
public sealed class Input(JsonElement body)
{
    public Dictionary<string, string[]> Errors { get; } = [];

    public string? Text(string name)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString()!.Trim() is { Length: > 0 } text)
        {
            return text;
        }

        Errors[name] = [$"{name} is required."];
        return null;
    }

    public int Integer(string name, int min)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number) && number >= min)
        {
            return number;
        }

        Errors[name] = [$"{name} must be a whole number of at least {min}."];
        return 0;
    }

    public long[] Ids(string name)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() > 0 && value.EnumerateArray().All(id => id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out _)))
        {
            return [.. value.EnumerateArray().Select(id => id.GetInt64()).Distinct()];
        }

        Errors[name] = [$"{name} must list at least one id."];
        return [];
    }

    public long Id(string name)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id))
        {
            return id;
        }

        Errors[name] = [$"{name} is required."];
        return 0;
    }
}

/// <summary>The catalog, in SQLite.</summary>
public sealed class Catalog(string path)
{
    private readonly string connection = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

    public void Create() => Execute("""
        CREATE TABLE IF NOT EXISTS authors (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS books (id INTEGER PRIMARY KEY AUTOINCREMENT, isbn TEXT NOT NULL UNIQUE, title TEXT NOT NULL, year INTEGER NOT NULL, author_ids TEXT NOT NULL, copies INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS members (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE COLLATE NOCASE);
        CREATE TABLE IF NOT EXISTS loans (id INTEGER PRIMARY KEY AUTOINCREMENT, book_id INTEGER NOT NULL, member_id INTEGER NOT NULL, loaned_on TEXT NOT NULL, due_on TEXT NOT NULL, returned_on TEXT);
        """);

    public List<Author> Authors() => Query("SELECT id, name FROM authors", r => new Author(r.GetInt64(0), r.GetString(1)));

    public Author? Author(long id) => Authors().FirstOrDefault(author => author.Id == id);

    public Author AddAuthor(string name) => new(Insert("INSERT INTO authors (name) VALUES ($a) RETURNING id", name), name);

    public void SaveAuthor(Author author) => Execute("UPDATE authors SET name = $a WHERE id = $b", author.Name, author.Id);

    public void DeleteAuthor(long id) => Execute("DELETE FROM authors WHERE id = $a", id);

    public List<Book> Books() => Query("SELECT id, isbn, title, year, author_ids, copies FROM books ORDER BY id",
        r => new Book(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), JsonSerializer.Deserialize<long[]>(r.GetString(4))!, r.GetInt32(5)));

    public Book? Book(long id) => Books().FirstOrDefault(book => book.Id == id);

    public Book? AddBook(Book book) => Unique(() => book with
    {
        Id = Insert("INSERT INTO books (isbn, title, year, author_ids, copies) VALUES ($a, $b, $c, $d, $e) RETURNING id",
            book.Isbn, book.Title, book.Year, JsonSerializer.Serialize(book.AuthorIds), book.Copies),
    });

    public Book? SaveBook(Book book) => Unique(() =>
    {
        Execute("UPDATE books SET isbn = $a, title = $b, year = $c, author_ids = $d, copies = $e WHERE id = $f",
            book.Isbn, book.Title, book.Year, JsonSerializer.Serialize(book.AuthorIds), book.Copies, book.Id);
        return book;
    });

    public void DeleteBook(long id) => Execute("DELETE FROM books WHERE id = $a", id);

    public List<Member> Members() => Query("SELECT id, name, email FROM members", r => new Member(r.GetInt64(0), r.GetString(1), r.GetString(2)));

    public Member? Member(long id) => Members().FirstOrDefault(member => member.Id == id);

    public Member? AddMember(string name, string email) =>
        Unique(() => new Member(Insert("INSERT INTO members (name, email) VALUES ($a, $b) RETURNING id", name, email), name, email));

    public Member? SaveMember(Member member) => Unique(() =>
    {
        Execute("UPDATE members SET name = $a, email = $b WHERE id = $c", member.Name, member.Email, member.Id);
        return member;
    });

    public void DeleteMember(long id) => Execute("DELETE FROM members WHERE id = $a", id);

    public List<Loan> OpenLoans() => Query("SELECT id, book_id, member_id, loaned_on, due_on, returned_on FROM loans WHERE returned_on IS NULL ORDER BY id", ReadLoan);

    public Loan? Loan(long id) => Query("SELECT id, book_id, member_id, loaned_on, due_on, returned_on FROM loans WHERE id = $a", ReadLoan, id).FirstOrDefault();

    public Loan AddLoan(long bookId, long memberId, DateOnly today)
    {
        var (on, due) = (Date(today), Date(today.AddDays(14)));
        return new(Insert("INSERT INTO loans (book_id, member_id, loaned_on, due_on) VALUES ($a, $b, $c, $d) RETURNING id", bookId, memberId, on, due), bookId, memberId, on, due, null);
    }

    public Loan Return(Loan loan, DateOnly today)
    {
        Execute("UPDATE loans SET returned_on = $a WHERE id = $b", Date(today), loan.Id);
        return loan with { ReturnedOn = Date(today) };
    }

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Loan ReadLoan(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));

    /// <summary>Runs a write; null when it breaks a unique constraint.</summary>
    private static T? Unique<T>(Func<T> write) where T : class
    {
        try
        {
            return write();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return null;
        }
    }

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

    private long Insert(string sql, params object[] values)
    {
        using var db = Open();
        using var command = Command(db, sql, values);
        return (long)command.ExecuteScalar()!;
    }

    private void Execute(string sql, params object[] values)
    {
        using var db = Open();
        using var command = Command(db, sql, values);
        command.ExecuteNonQuery();
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
