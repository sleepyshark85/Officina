using System.Globalization;
using System.Text.Json;
using TodoApi;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PORT") ?? "5000"}");
var store = new TodoStore(Environment.GetEnvironmentVariable("TODO_DB") ?? "todos.db");
store.Create();
var app = builder.Build();

app.MapPost("/todos", async (HttpRequest request) =>
{
    if (await BodyAsync(request) is not { } body)
    {
        return Invalid(new() { ["body"] = ["The body must be JSON."] });
    }

    var input = TodoInput.Read(body, creating: true);
    if (input.Errors.Count > 0)
    {
        return Invalid(input.Errors);
    }

    var todo = store.Add(input.Title!, input.Due, DateTimeOffset.UtcNow);
    return Results.Created($"/todos/{todo.Id}", todo);
});

app.MapGet("/todos/{id}", (string id) => Id(id) is { } key && store.Find(key) is { } todo ? Results.Ok(todo) : Results.NotFound());

app.MapPatch("/todos/{id}", async (string id, HttpRequest request) =>
{
    if (Id(id) is not { } key || store.Find(key) is not { } todo)
    {
        return Results.NotFound();
    }

    if (await BodyAsync(request) is not { } body)
    {
        return Invalid(new() { ["body"] = ["The body must be JSON."] });
    }

    var input = TodoInput.Read(body, creating: false);
    if (input.Errors.Count > 0)
    {
        return Invalid(input.Errors);
    }

    todo = todo with { Title = input.Title ?? todo.Title, Done = input.Done ?? todo.Done, Due = input.HasDue ? input.Due : todo.Due };
    store.Save(todo);
    return Results.Ok(todo);
});

app.MapDelete("/todos/{id}", (string id) => Id(id) is { } key && store.Delete(key) ? Results.NoContent() : Results.NotFound());

app.MapGet("/todos", (HttpRequest request) =>
{
    var errors = new Dictionary<string, string[]>();
    var query = request.Query;
    bool? done = null;
    if (query.TryGetValue("done", out var doneText))
    {
        if (bool.TryParse(doneText, out var parsed))
        {
            done = parsed;
        }
        else
        {
            errors["done"] = ["Done must be true or false."];
        }
    }

    string? dueBefore = query["dueBefore"];
    if (dueBefore is not null && !TodoInput.IsDate(dueBefore))
    {
        errors["dueBefore"] = ["The date must be yyyy-MM-dd."];
    }

    var page = Number(query["page"], 1, 1, int.MaxValue, "page", errors);
    var pageSize = Number(query["pageSize"], 20, 1, 100, "pageSize", errors);
    if (errors.Count > 0)
    {
        return Invalid(errors);
    }

    var (items, total) = store.List(done, dueBefore, page, pageSize);
    return Results.Ok(new { items, total });
});

app.Run();

static long? Id(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

static int Number(string? text, int fallback, int min, int max, string name, Dictionary<string, string[]> errors)
{
    if (text is null)
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

static IResult Invalid(Dictionary<string, string[]> errors) => Results.Json(new { errors }, statusCode: StatusCodes.Status400BadRequest);

static async Task<JsonElement?> BodyAsync(HttpRequest request)
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        return document.RootElement.Clone();
    }
    catch (JsonException)
    {
        return null;
    }
}
