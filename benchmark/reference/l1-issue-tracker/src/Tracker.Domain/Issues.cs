using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tracker.Domain;

public sealed record User(long Id, string Name, string Email, bool Admin);

public sealed record Project(string Key, string Name, long CreatorId);

public sealed record Issue(
    string Id, string ProjectKey, int Number, string Title, string Description, string Type, string Priority, long? AssigneeId,
    string[] Labels, string Status, long ReporterId, string CreatedAt, string UpdatedAt);

public sealed record Comment(long Id, string IssueId, long AuthorId, string Body, string CreatedAt, string UpdatedAt);

public sealed record Change(long UserId, string At, string Field, JsonElement Old, JsonElement New);

/// <summary>The status workflow: open → in_progress → in_review → done, back from in_review, and closed from any state.</summary>
public static class Workflow
{
    public static readonly string[] Statuses = ["open", "in_progress", "in_review", "done", "closed"];

    public static bool CanMove(string from, string to) => (from, to) switch
    {
        ("open", "in_progress") or ("in_progress", "in_review") or ("in_review", "done") or ("in_review", "in_progress") => true,
        (not "closed", "closed") => true,
        _ => false,
    };
}

public static partial class Rules
{
    public static readonly string[] Types = ["bug", "task", "story"];

    public static readonly string[] Priorities = ["low", "medium", "high", "critical"];

    public static bool IsKey(string? key) => key is not null && Key().IsMatch(key);

    public static bool IsEmail(string? email) => email is not null && Email().IsMatch(email);

    [GeneratedRegex("^[A-Z]{2,10}$")]
    private static partial Regex Key();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex Email();
}

/// <summary>The fields of an issue that a request sets: all of them to create one, any of them to change one.</summary>
public sealed class IssueInput
{
    public Dictionary<string, string[]> Errors { get; } = [];

    public string? Title { get; private set; }

    public string? Description { get; private set; }

    public string? Type { get; private set; }

    public string? Priority { get; private set; }

    public bool SetsAssignee { get; private set; }

    public long? AssigneeId { get; private set; }

    public string[]? Labels { get; private set; }

    /// <summary>Reads the fields; <paramref name="isMember"/> says whether a user is a member of the issue's project.</summary>
    public static IssueInput Read(JsonElement body, bool creating, Func<long, bool> isMember)
    {
        var input = new IssueInput();
        if (body.ValueKind != JsonValueKind.Object)
        {
            input.Errors["body"] = ["The body must be a JSON object."];
            return input;
        }

        if (Field(body, "title", creating, input) is { } title)
        {
            input.Title = title.ValueKind == JsonValueKind.String && title.GetString()!.Trim() is { Length: > 0 and <= 200 } text ? text : Error(input, "title", "The title must have 1 to 200 characters.");
        }

        if (body.TryGetProperty("description", out var description))
        {
            input.Description = description.ValueKind switch
            {
                JsonValueKind.String => description.GetString(),
                JsonValueKind.Null => "",
                _ => Error(input, "description", "The description must be text."),
            };
        }
        else if (creating)
        {
            input.Description = "";
        }

        input.Type = OneOf(body, "type", Rules.Types, creating ? "task" : null, input);
        input.Priority = OneOf(body, "priority", Rules.Priorities, creating ? "medium" : null, input);
        if (body.TryGetProperty("assigneeId", out var assignee))
        {
            input.SetsAssignee = true;
            if (assignee.ValueKind == JsonValueKind.Number && assignee.TryGetInt64(out var id) && isMember(id))
            {
                input.AssigneeId = id;
            }
            else if (assignee.ValueKind != JsonValueKind.Null)
            {
                Error(input, "assigneeId", "The assignee must be a member of the project.");
            }
        }

        if (body.TryGetProperty("labels", out var labels))
        {
            if (labels.ValueKind == JsonValueKind.Array && labels.EnumerateArray().All(label => label.ValueKind == JsonValueKind.String && label.GetString()!.Trim().Length > 0))
            {
                input.Labels = [.. labels.EnumerateArray().Select(label => label.GetString()!.Trim()).Distinct()];
            }
            else
            {
                Error(input, "labels", "Labels must be a list of text.");
            }
        }
        else if (creating)
        {
            input.Labels = [];
        }

        return input;
    }

    private static JsonElement? Field(JsonElement body, string name, bool required, IssueInput input)
    {
        if (body.TryGetProperty(name, out var value))
        {
            return value;
        }

        if (required)
        {
            Error(input, name, $"The {name} is required.");
        }

        return null;
    }

    private static string? OneOf(JsonElement body, string name, string[] allowed, string? fallback, IssueInput input)
    {
        if (!body.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()) ? value.GetString() : Error(input, name, $"The {name} must be one of {string.Join(", ", allowed)}.");
    }

    private static string? Error(IssueInput input, string field, string message)
    {
        input.Errors[field] = [message];
        return null;
    }
}

/// <summary>A query of a project's issues: filters, a sort and a page.</summary>
public sealed record IssueQuery(string? Status, long? AssigneeId, string? Label, string? Type, string? Text, string Sort, int Page, int PageSize)
{
    public static readonly string[] Sorts = ["priority", "created", "updated"];

    public (List<Issue> Items, int Total) Apply(IEnumerable<Issue> issues)
    {
        var found = issues
            .Where(issue => Status is null || issue.Status == Status)
            .Where(issue => AssigneeId is null || issue.AssigneeId == AssigneeId)
            .Where(issue => Label is null || issue.Labels.Contains(Label))
            .Where(issue => Type is null || issue.Type == Type)
            .Where(issue => Text is null || issue.Title.Contains(Text, StringComparison.OrdinalIgnoreCase) || issue.Description.Contains(Text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var descending = Sort.StartsWith('-');
        Func<Issue, IComparable> key = Sort.TrimStart('-') switch
        {
            "priority" => issue => Array.IndexOf(Rules.Priorities, issue.Priority),
            "updated" => issue => issue.UpdatedAt,
            _ => issue => issue.Number,
        };
        var sorted = descending ? found.OrderByDescending(key).ThenBy(issue => issue.Number) : found.OrderBy(key).ThenBy(issue => issue.Number);
        return ([.. sorted.Skip((Page - 1) * PageSize).Take(PageSize)], found.Count);
    }
}
