using System.Globalization;
using System.Text.Json;
using Tracker;
using Tracker.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PORT") ?? "5000"}");
var store = new TrackerStore(Environment.GetEnvironmentVariable("TRACKER_DB") ?? "tracker.db");
store.Create();
var app = builder.Build();

// Every request but signing up needs a user's token.
app.Use(async (context, next) =>
{
    if (!(context.Request.Method == "POST" && context.Request.Path == "/users"))
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal) || store.UserByToken(header["Bearer ".Length..].Trim()) is not { } user)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { error = "A valid bearer token is required." });
            return;
        }

        context.Items["user"] = user;
    }

    await next();
});

app.MapPost("/users", async (HttpRequest request) =>
{
    if (await BodyAsync(request) is not { } body)
    {
        return Invalid("body", "The body must be JSON.");
    }

    var errors = new Dictionary<string, string[]>();
    var name = Text(body, "name", errors);
    var email = Text(body, "email", errors);
    if (email is not null && !Rules.IsEmail(email))
    {
        errors["email"] = ["The email address is not valid."];
    }

    if (errors.Count > 0)
    {
        return Results.Json(new { errors }, statusCode: 400);
    }

    if (store.AddUser(name!, email!) is not var (user, token))
    {
        return Conflict("The email is taken.");
    }

    return Results.Created($"/users/{user.Id}", new { user.Id, user.Name, user.Email, user.Admin, token });
});

app.MapGet("/users/{id:long}", (long id) => store.User(id) is { } user ? Results.Ok(user) : NotFound());

app.MapPost("/projects", async (HttpContext context) =>
{
    if (await BodyAsync(context.Request) is not { } body)
    {
        return Invalid("body", "The body must be JSON.");
    }

    var errors = new Dictionary<string, string[]>();
    var key = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
    if (!Rules.IsKey(key))
    {
        errors["key"] = ["The key must be 2 to 10 capital letters."];
    }

    var name = Text(body, "name", errors);
    if (errors.Count > 0)
    {
        return Results.Json(new { errors }, statusCode: 400);
    }

    var project = new Project(key!, name!, Me(context).Id);
    return store.AddProject(project) ? Results.Created($"/projects/{key}", project) : Conflict($"The key {key} is taken.");
});

app.MapGet("/projects", (HttpContext context) =>
{
    var items = store.Projects().Where(project => Sees(Me(context), project.Key)).ToList();
    return Results.Ok(new { items, total = items.Count });
});

app.MapGet("/projects/{key}", (string key, HttpContext context) => Visible(context, key) ?? Results.Ok(store.Project(key)));

app.MapPost("/projects/{key}/members", async (string key, HttpContext context) =>
{
    var me = Me(context);
    if (store.Project(key) is not { } project)
    {
        return NotFound();
    }

    if (project.CreatorId != me.Id && !me.Admin)
    {
        return Forbidden();
    }

    if (await BodyAsync(context.Request) is not { } body || !body.TryGetProperty("userId", out var u) || !u.TryGetInt64(out var userId) || store.User(userId) is null)
    {
        return Invalid("userId", "userId must name a user.");
    }

    store.AddMember(key, userId);
    return Results.NoContent();
});

app.MapPost("/projects/{key}/issues", async (string key, HttpContext context) =>
{
    if (Visible(context, key) is { } refused)
    {
        return refused;
    }

    if (await BodyAsync(context.Request) is not { } body)
    {
        return Invalid("body", "The body must be JSON.");
    }

    var input = IssueInput.Read(body, creating: true, id => store.IsMember(key, id));
    if (input.Errors.Count > 0)
    {
        return Results.Json(new { errors = input.Errors }, statusCode: 400);
    }

    var issue = store.AddIssue(key, input, Me(context).Id);
    return Results.Created($"/issues/{issue.Id}", issue);
});

app.MapGet("/projects/{key}/issues", (string key, HttpContext context) =>
{
    if (Visible(context, key) is { } refused)
    {
        return refused;
    }

    var query = context.Request.Query;
    var errors = new Dictionary<string, string[]>();
    var status = query["status"].FirstOrDefault();
    if (status is not null && !Workflow.Statuses.Contains(status))
    {
        errors["status"] = ["Unknown status."];
    }

    var type = query["type"].FirstOrDefault();
    if (type is not null && !Rules.Types.Contains(type))
    {
        errors["type"] = ["Unknown type."];
    }

    long? assignee = null;
    if (query["assigneeId"].FirstOrDefault() is { } assigneeText)
    {
        if (long.TryParse(assigneeText, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            assignee = id;
        }
        else
        {
            errors["assigneeId"] = ["assigneeId must be a user id."];
        }
    }

    var sort = query["sort"].FirstOrDefault() ?? "created";
    if (!IssueQuery.Sorts.Contains(sort.TrimStart('-')))
    {
        errors["sort"] = ["sort must be priority, created or updated, with an optional '-'."];
    }

    var page = Number(query["page"], 1, 1, int.MaxValue, "page", errors);
    var pageSize = Number(query["pageSize"], 20, 1, 100, "pageSize", errors);
    if (errors.Count > 0)
    {
        return Results.Json(new { errors }, statusCode: 400);
    }

    var (items, total) = new IssueQuery(status, assignee, query["label"].FirstOrDefault(), type, query["q"].FirstOrDefault(), sort, page, pageSize).Apply(store.Issues(key));
    return Results.Ok(new { items, total });
});

app.MapGet("/issues/{id}", (string id, HttpContext context) => Find(context, id, out var issue) ?? Results.Ok(issue));

app.MapPatch("/issues/{id}", async (string id, HttpContext context) =>
{
    if (Find(context, id, out var issue) is { } refused)
    {
        return refused;
    }

    if (await BodyAsync(context.Request) is not { } body)
    {
        return Invalid("body", "The body must be JSON.");
    }

    var input = IssueInput.Read(body, creating: false, user => store.IsMember(issue!.ProjectKey, user));
    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("status", out _))
    {
        input.Errors["status"] = ["The status changes only through a transition."];
    }

    if (input.Errors.Count > 0)
    {
        return Results.Json(new { errors = input.Errors }, statusCode: 400);
    }

    var changed = issue! with
    {
        Title = input.Title ?? issue.Title, Description = input.Description ?? issue.Description, Type = input.Type ?? issue.Type,
        Priority = input.Priority ?? issue.Priority, AssigneeId = input.SetsAssignee ? input.AssigneeId : issue.AssigneeId, Labels = input.Labels ?? issue.Labels,
    };
    return Results.Ok(store.Change(issue, changed, Me(context).Id));
});

app.MapPost("/issues/{id}/transitions", async (string id, HttpContext context) =>
{
    if (Find(context, id, out var issue) is { } refused)
    {
        return refused;
    }

    var to = await BodyAsync(context.Request) is { ValueKind: JsonValueKind.Object } body && body.TryGetProperty("to", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
    if (to is null || !Workflow.Statuses.Contains(to))
    {
        return Invalid("to", $"to must be one of {string.Join(", ", Workflow.Statuses)}.");
    }

    return Workflow.CanMove(issue!.Status, to)
        ? Results.Ok(store.Change(issue, issue with { Status = to }, Me(context).Id))
        : Conflict($"An issue cannot move from {issue.Status} to {to}.");
});

app.MapGet("/issues/{id}/history", (string id, HttpContext context) =>
{
    if (Find(context, id, out var issue) is { } refused)
    {
        return refused;
    }

    var items = store.History(issue!.Id);
    return Results.Ok(new { items, total = items.Count });
});

app.MapPost("/issues/{id}/comments", async (string id, HttpContext context) =>
{
    if (Find(context, id, out var issue) is { } refused)
    {
        return refused;
    }

    var errors = new Dictionary<string, string[]>();
    var text = await BodyAsync(context.Request) is { } body ? Text(body, "body", errors) : null;
    if (text is null)
    {
        return Invalid("body", "The comment needs a body.");
    }

    var comment = store.AddComment(issue!.Id, Me(context).Id, text);
    return Results.Created($"/issues/{issue.Id}/comments/{comment.Id}", comment);
});

app.MapGet("/issues/{id}/comments", (string id, HttpContext context) =>
{
    if (Find(context, id, out var issue) is { } refused)
    {
        return refused;
    }

    var items = store.Comments(issue!.Id);
    return Results.Ok(new { items, total = items.Count });
});

app.MapPut("/issues/{id}/comments/{commentId:long}", async (string id, long commentId, HttpContext context) =>
{
    if (Find(context, id, out var issue) is { } refused)
    {
        return refused;
    }

    if (store.Comments(issue!.Id).FirstOrDefault(comment => comment.Id == commentId) is not { } comment)
    {
        return NotFound();
    }

    if (comment.AuthorId != Me(context).Id)
    {
        return Forbidden();
    }

    var errors = new Dictionary<string, string[]>();
    var text = await BodyAsync(context.Request) is { } body ? Text(body, "body", errors) : null;
    return text is null ? Invalid("body", "The comment needs a body.") : Results.Ok(store.SaveComment(comment with { Body = text }));
});

app.Run();

User Me(HttpContext context) => (User)context.Items["user"]!;

bool Sees(User user, string key) => user.Admin || store.IsMember(key, user.Id);

// 404 for a project that does not exist, 403 for one the user does not see; null when the user sees it.
IResult? Visible(HttpContext context, string key) => store.Project(key) is null ? NotFound() : Sees(Me(context), key) ? null : Forbidden();

IResult? Find(HttpContext context, string id, out Issue? issue)
{
    issue = store.Issue(id);
    return issue is null ? NotFound() : Visible(context, issue.ProjectKey);
}

static string? Text(JsonElement body, string name, Dictionary<string, string[]> errors)
{
    if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 0 } text)
    {
        return text;
    }

    errors[name] = [$"{name} is required."];
    return null;
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

static IResult Invalid(string field, string message) => Results.Json(new { errors = new Dictionary<string, string[]> { [field] = [message] } }, statusCode: 400);

static IResult NotFound() => Results.Json(new { error = "Not found." }, statusCode: 404);

static IResult Forbidden() => Results.Json(new { error = "You may not do this." }, statusCode: 403);

static IResult Conflict(string reason) => Results.Json(new { error = reason }, statusCode: 409);
