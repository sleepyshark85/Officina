using System.Text.Json;
using TodoApi;
using Xunit;

namespace TodoApi.Tests;

public class TodoInputTests
{
    [Fact]
    public void Names_each_invalid_field()
    {
        var input = TodoInput.Read(JsonDocument.Parse("""{ "title": "  ", "due": "31/01/2030" }""").RootElement, creating: true);
        Assert.Equal(["due", "title"], input.Errors.Keys.Order());
    }

    [Fact]
    public void Trims_the_title()
    {
        var input = TodoInput.Read(JsonDocument.Parse("""{ "title": "  Buy milk  " }""").RootElement, creating: true);
        Assert.Equal("Buy milk", input.Title);
    }

    [Fact]
    public void Stores_and_lists_by_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"todo-{Guid.NewGuid():N}.db");
        var store = new TodoStore(path);
        store.Create();
        var first = store.Add("a", "2030-01-01", DateTimeOffset.UnixEpoch);
        store.Add("b", null, DateTimeOffset.UnixEpoch);
        Assert.Equal((first.Id, 2L), (store.List(null, null, 1, 1).Items[0].Id, store.List(null, null, 1, 20).Total));
        Assert.Single(store.List(null, "2030-01-02", 1, 20).Items);
    }
}
