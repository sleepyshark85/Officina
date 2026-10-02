using System.Globalization;
using System.Text.Json;
using Library;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PORT") ?? "5000"}");
var catalog = new Catalog(Environment.GetEnvironmentVariable("LIBRARY_DB") ?? "library.db");
catalog.Create();
var app = builder.Build();

DateOnly Today() => Environment.GetEnvironmentVariable("LIBRARY_TODAY") is { } today
    ? DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture)
    : DateOnly.FromDateTime(DateTime.UtcNow);

// Authors.
app.MapPost("/authors", async (HttpRequest request) =>
{
    var input = await BodyAsync(request);
    if (input?.Text("name") is not { } name)
    {
        return Invalid(input);
    }

    var author = catalog.AddAuthor(name);
    return Results.Created($"/authors/{author.Id}", author);
});
app.MapGet("/authors/{id:long}", (long id) => catalog.Author(id) is { } author ? Results.Ok(author) : NotFound());
app.MapPut("/authors/{id:long}", async (long id, HttpRequest request) =>
{
    if (catalog.Author(id) is null)
    {
        return NotFound();
    }

    var input = await BodyAsync(request);
    if (input?.Text("name") is not { } name)
    {
        return Invalid(input);
    }

    var author = new Author(id, name);
    catalog.SaveAuthor(author);
    return Results.Ok(author);
});
app.MapDelete("/authors/{id:long}", (long id) =>
{
    if (catalog.Author(id) is null)
    {
        return NotFound();
    }

    if (catalog.Books().Any(book => book.AuthorIds.Contains(id)))
    {
        return Conflict("The author has books.");
    }

    catalog.DeleteAuthor(id);
    return Results.NoContent();
});

// Books.
app.MapPost("/books", async (HttpRequest request) =>
{
    var (book, error) = await ReadBookAsync(request, 0);
    if (error is not null)
    {
        return error;
    }

    return catalog.AddBook(book!) is { } added ? Results.Created($"/books/{added.Id}", added) : Conflict("The ISBN is taken.");
});
app.MapGet("/books/{id:long}", (long id) => catalog.Book(id) is { } book ? Results.Ok(book) : NotFound());
app.MapPut("/books/{id:long}", async (long id, HttpRequest request) =>
{
    if (catalog.Book(id) is null)
    {
        return NotFound();
    }

    var (book, error) = await ReadBookAsync(request, id);
    return error ?? (catalog.SaveBook(book!) is { } saved ? Results.Ok(saved) : Conflict("The ISBN is taken."));
});
app.MapDelete("/books/{id:long}", (long id) =>
{
    if (catalog.Book(id) is null)
    {
        return NotFound();
    }

    if (catalog.OpenLoans().Any(loan => loan.BookId == id))
    {
        return Conflict("The book is on loan.");
    }

    catalog.DeleteBook(id);
    return Results.NoContent();
});
app.MapGet("/books", (HttpRequest request) =>
{
    var errors = new Dictionary<string, string[]>();
    var page = Number(request.Query["page"], 1, 1, int.MaxValue, "page", errors);
    var pageSize = Number(request.Query["pageSize"], 20, 1, 100, "pageSize", errors);
    bool? available = null;
    if (request.Query["available"] is { Count: > 0 } availableText)
    {
        if (bool.TryParse(availableText, out var wanted))
        {
            available = wanted;
        }
        else
        {
            errors["available"] = ["available must be true or false."];
        }
    }

    if (errors.Count > 0)
    {
        return Results.Json(new { errors }, statusCode: StatusCodes.Status400BadRequest);
    }

    var q = request.Query["q"].ToString().Trim();
    var authors = catalog.Authors().ToDictionary(author => author.Id, author => author.Name);
    var loans = catalog.OpenLoans();
    var found = catalog.Books()
        .Where(book => q.Length == 0 || book.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || book.AuthorIds.Any(id => authors.TryGetValue(id, out var name) && name.Contains(q, StringComparison.OrdinalIgnoreCase)))
        .Where(book => available is null || (book.Copies > loans.Count(loan => loan.BookId == book.Id)) == available)
        .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase).ThenBy(book => book.Id)
        .ToList();
    return Results.Ok(new { items = found.Skip((page - 1) * pageSize).Take(pageSize), total = found.Count });
});

// Members.
app.MapPost("/members", async (HttpRequest request) =>
{
    var (name, email, error) = await ReadMemberAsync(request);
    if (error is not null)
    {
        return error;
    }

    return catalog.AddMember(name!, email!) is { } added ? Results.Created($"/members/{added.Id}", added) : Conflict("The email is taken.");
});
app.MapGet("/members/{id:long}", (long id) => catalog.Member(id) is { } member ? Results.Ok(member) : NotFound());
app.MapPut("/members/{id:long}", async (long id, HttpRequest request) =>
{
    if (catalog.Member(id) is null)
    {
        return NotFound();
    }

    var (name, email, error) = await ReadMemberAsync(request);
    return error ?? (catalog.SaveMember(new Member(id, name!, email!)) is { } saved ? Results.Ok(saved) : Conflict("The email is taken."));
});
app.MapDelete("/members/{id:long}", (long id) =>
{
    if (catalog.Member(id) is null)
    {
        return NotFound();
    }

    if (catalog.OpenLoans().Any(loan => loan.MemberId == id))
    {
        return Conflict("The member has books on loan.");
    }

    catalog.DeleteMember(id);
    return Results.NoContent();
});
app.MapGet("/members/{id:long}/loans", (long id) =>
    catalog.Member(id) is null ? NotFound() : Results.Ok(catalog.OpenLoans().Where(loan => loan.MemberId == id)));

// Loans.
app.MapPost("/loans", async (HttpRequest request) =>
{
    if (await BodyAsync(request) is not { } input)
    {
        return Invalid(null);
    }

    var (bookId, memberId) = (input.Id("bookId"), input.Id("memberId"));
    if (input.Errors.Count > 0)
    {
        return Invalid(input);
    }

    if (catalog.Book(bookId) is not { } book || catalog.Member(memberId) is null)
    {
        return NotFound();
    }

    var today = Today();
    var loans = catalog.OpenLoans();
    var mine = loans.Where(loan => loan.MemberId == memberId).ToList();
    if (mine.Count >= 3)
    {
        return Conflict("The member already has 3 loans.");
    }

    if (mine.Any(loan => string.CompareOrdinal(loan.DueOn, Catalog.Date(today)) < 0))
    {
        return Conflict("The member has an overdue loan.");
    }

    if (loans.Count(loan => loan.BookId == bookId) >= book.Copies)
    {
        return Conflict("No copy is free.");
    }

    var added = catalog.AddLoan(bookId, memberId, today);
    return Results.Created($"/loans/{added.Id}", added);
});
app.MapPost("/loans/{id:long}/return", (long id) =>
{
    if (catalog.Loan(id) is not { } loan)
    {
        return NotFound();
    }

    return loan.ReturnedOn is null ? Results.Ok(catalog.Return(loan, Today())) : Conflict("The loan was already returned.");
});

app.Run();

async Task<(Book? Book, IResult? Error)> ReadBookAsync(HttpRequest request, long id)
{
    if (await BodyAsync(request) is not { } input)
    {
        return (null, Invalid(null));
    }

    var isbn = input.Text("isbn")?.Replace("-", "", StringComparison.Ordinal);
    if (isbn is not null && !Rules.IsIsbn13(isbn))
    {
        input.Errors["isbn"] = ["The ISBN must be a valid ISBN-13."];
    }

    var title = input.Text("title");
    var year = input.Integer("year", 0);
    var copies = input.Integer("copies", 1);
    var authorIds = input.Ids("authorIds");
    var known = catalog.Authors().Select(author => author.Id).ToHashSet();
    if (authorIds.Length > 0 && !authorIds.All(known.Contains))
    {
        input.Errors["authorIds"] = ["Every author must exist."];
    }

    return input.Errors.Count > 0 ? (null, Invalid(input)) : (new Book(id, isbn!, title!, year, authorIds, copies), null);
}

async Task<(string? Name, string? Email, IResult? Error)> ReadMemberAsync(HttpRequest request)
{
    if (await BodyAsync(request) is not { } input)
    {
        return (null, null, Invalid(null));
    }

    var name = input.Text("name");
    var email = input.Text("email");
    if (email is not null && !Rules.IsEmail(email))
    {
        input.Errors["email"] = ["The email address is not valid."];
    }

    return input.Errors.Count > 0 ? (null, null, Invalid(input)) : (name, email, null);
}

static async Task<Input?> BodyAsync(HttpRequest request)
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        return new Input(document.RootElement.Clone());
    }
    catch (JsonException)
    {
        return null;
    }
}

static int Number(string? text, int fallback, int min, int max, string name, Dictionary<string, string[]> errors)
{
    if (string.IsNullOrEmpty(text))
    {
        return fallback;
    }

    if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
    {
        return value;
    }

    errors[name] = [$"{name} must be a whole number from {min} to {max}."];
    return fallback;
}

static IResult Invalid(Input? input) => Results.Json(
    new { errors = input?.Errors ?? new Dictionary<string, string[]> { ["body"] = ["The body must be JSON."] } }, statusCode: StatusCodes.Status400BadRequest);

static IResult NotFound() => Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound);

static IResult Conflict(string reason) => Results.Json(new { error = reason }, statusCode: StatusCodes.Status409Conflict);
