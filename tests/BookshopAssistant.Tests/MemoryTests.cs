using Sleepyshark.Officina;
using Sleepyshark.Officina.Memory.Files;
using Sleepyshark.Officina.Testing;
using static BookshopAssistant.Tests.ConsoleSession;

namespace BookshopAssistant.Tests;

/// <summary>
/// Memory per staff member, end to end. Each <see cref="ConsoleSession.RunAsync"/> is an application start; what one
/// remembers, the next finds only in the memory store.
/// </summary>
public class MemoryTests(BookshopDatabase database) : IClassFixture<BookshopDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [DatabaseFact]
    public async Task APP_11_memory_shows_what_is_remembered_for_the_staff_member_at_the_counter()
    {
        var memory = new InMemoryMemoryStore();
        await memory.WriteAsync("sam", "preferences.md", "Prices with tax.\nBrief answers.\n", Ct);
        await memory.WriteAsync("ana", "notes.md", "Ana's notes.", Ct);

        var sam = await RunAsync(database, Model(), ["Sam", "/memory", "/quit"], memory: memory);
        var ben = await RunAsync(database, Model(), ["Ben", "/memory", "/quit"], memory: memory);

        InOrder(sam, "you> /memory\n", "Remembered:\n", "/memories/preferences.md\n", "  Prices with tax.\n", "  Brief answers.\n", "you> /quit");
        Assert.DoesNotContain("Ana's", sam, StringComparison.Ordinal);
        InOrder(ben, "you> /memory\n", "Nothing remembered yet.\n");
    }

    [DatabaseFact]
    public async Task APP_11_a_preference_saved_in_one_session_is_applied_in_a_new_one()
    {
        var folder = Directory.CreateTempSubdirectory("bookshop-memory-");
        try
        {
            var (save, view) = ($"save-{Guid.NewGuid():N}", $"view-{Guid.NewGuid():N}");
            var first = Model()
                .Reply(SayThenCall("I'll remember that.", Call(save, "memory", new { command = "create", path = "/memories/preferences.md", file_text = "Show prices with tax.\n" })))
                .Reply("Noted: prices with tax from now on.");
            await RunAsync(database, first, ["Sam", "I prefer prices with tax.", "/quit"], memory: new FileMemoryStore(folder.FullName));

            var second = Model()
                .CallTools(new ToolCall(view, "memory", """{"command":"view","path":"/memories/preferences.md"}"""))
                .Reply("The Winter Archive costs £7.54 with tax.");
            var transcript = await RunAsync(database, second, ["sam", "What does book 144 cost?", "/memory", "/quit"], memory: new FileMemoryStore(folder.FullName));

            Assert.Equal("Here's the content of /memories/preferences.md with line numbers:\n     1\tShow prices with tax.", LastResults(second).Single().Content);
            InOrder(transcript, $"  > memory ", "  < memory: ok", "costs £7.54 with tax.", "/memories/preferences.md\n", "  Show prices with tax.\n");

            // The run context names who is at the counter; memory never enters the instructions.
            Assert.Contains("The staff member using the assistant is sam.", second.Requests[0].Messages[1].Text, StringComparison.Ordinal);
            Assert.All([.. first.Requests, .. second.Requests], request => Assert.Equal(first.Requests[0].Prefix.Instructions, request.Prefix.Instructions));
            Assert.DoesNotContain("Show prices", first.Requests[0].Prefix.Instructions, StringComparison.Ordinal);

            // The write was audited before it ran, in Sam's scope.
            Assert.Equal("sam", await database.ScalarAsync<string>("select memory_scope from audit where call_id = $1 and kind = 'ToolStarted'", save));
            Assert.True(await database.ScalarAsync<bool>(
                "select (select id from audit where call_id = $1 and kind = 'ToolStarted') < (select id from audit where call_id = $1 and kind = 'ToolEnded')", save));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
