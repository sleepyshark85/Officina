using System.Text.Json;
using Shortener;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://127.0.0.1:{Environment.GetEnvironmentVariable("PORT") ?? "5000"}");
var store = new LinkStore(Environment.GetEnvironmentVariable("SHORTENER_DB") ?? "links.db");
store.Create();
var key = Environment.GetEnvironmentVariable("SHORTENER_KEY");
var app = builder.Build();

// Every request to the API needs the key; redirects do not.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && (string.IsNullOrEmpty(key) || context.Request.Headers["X-Api-Key"] != key))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = "A valid X-Api-Key header is required." });
        return;
    }

    await next();
});

app.MapPost("/api/links", async (HttpRequest request) =>
{
    JsonElement body;
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body);
        body = document.RootElement.Clone();
    }
    catch (JsonException)
    {
        return Invalid("The body must be JSON.");
    }

    var url = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
    if (!LinkRules.IsUrl(url))
    {
        return Invalid("The url must be an absolute http or https URL.");
    }

    if (body.TryGetProperty("code", out var c) && c.ValueKind != JsonValueKind.Null)
    {
        var code = c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (!LinkRules.IsCode(code))
        {
            return Invalid("A code must be 3 to 32 letters, digits, '-' or '_'.");
        }

        return store.TryAdd(code!, url!, out var chosen)
            ? Created(request, chosen)
            : Results.Json(new { error = $"The code {code} is taken." }, statusCode: StatusCodes.Status409Conflict);
    }

    while (true)
    {
        if (store.TryAdd(LinkRules.NewCode(), url!, out var link))
        {
            return Created(request, link);
        }
    }
});

app.MapGet("/api/links", (HttpRequest request) => Results.Ok(store.All().Select(link => View(request, link))));

app.MapGet("/api/links/{code}", (string code, HttpRequest request) => store.Find(code) is { } link ? Results.Ok(View(request, link)) : NotFound());

app.MapDelete("/api/links/{code}", (string code) => store.Delete(code) ? Results.NoContent() : NotFound());

app.MapGet("/{code}", (string code) =>
{
    if (store.Find(code) is not { } link)
    {
        return NotFound();
    }

    store.Visit(code);
    return Results.Redirect(link.Url);
});

app.Run();

static string ShortUrl(HttpRequest request, string code) => $"{request.Scheme}://{request.Host}/{code}";

static object View(HttpRequest request, Link link) =>
    new { link.Code, link.Url, shortUrl = ShortUrl(request, link.Code), link.Visits, link.LastVisitedAt, link.CreatedAt };

static IResult Created(HttpRequest request, Link link) =>
    Results.Created($"/api/links/{link.Code}", new { link.Code, link.Url, shortUrl = ShortUrl(request, link.Code) });

static IResult Invalid(string reason) => Results.Json(new { error = reason }, statusCode: StatusCodes.Status400BadRequest);

static IResult NotFound() => Results.Json(new { error = "There is no such link." }, statusCode: StatusCodes.Status404NotFound);
