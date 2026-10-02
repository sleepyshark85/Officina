using System.Text.Json;
using Tracker.Domain;
using Xunit;

namespace Tracker.Tests;

public class DomainTests
{
    [Theory]
    [InlineData("open", "in_progress", true)]
    [InlineData("in_review", "in_progress", true)]
    [InlineData("open", "done", false)]
    [InlineData("done", "closed", true)]
    [InlineData("closed", "open", false)]
    public void The_status_moves_only_along_the_workflow(string from, string to, bool allowed) => Assert.Equal(allowed, Workflow.CanMove(from, to));

    [Fact]
    public void An_assignee_must_be_a_member()
    {
        var input = IssueInput.Read(JsonDocument.Parse("""{ "title": "x", "assigneeId": 7 }""").RootElement, creating: true, _ => false);
        Assert.Equal(["assigneeId"], input.Errors.Keys);
    }

    [Fact]
    public void Sorts_by_priority_descending()
    {
        Issue Make(int number, string priority) => new($"A-{number}", "A", number, $"t{number}", "", "bug", priority, null, [], "open", 1, "", "");
        var (items, total) = new IssueQuery(null, null, null, null, null, "-priority", 1, 20).Apply([Make(1, "low"), Make(2, "critical")]);
        Assert.Equal(["A-2", "A-1"], items.Select(issue => issue.Id));
        Assert.Equal(2, total);
    }
}
