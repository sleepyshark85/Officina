using Sleepyshark.Officina.Core.Configuration;
using Sleepyshark.Officina.Core.Extensibility;
using Sleepyshark.Officina.Core.Messages;

namespace Sleepyshark.Officina.Testing.Tests;

public class ScriptedModelProviderTests
{
    private static readonly ModelRequest Request = new(new ModelProfile(), "Be brief.", [Message.User("hi")], []);

    [Fact]
    public async Task Replies_are_given_in_script_order()
    {
        var model = new ScriptedModelProvider().Reply("one").Reply("two");

        Assert.Equal("one", await ReplyTextAsync(model));
        Assert.Equal("two", await ReplyTextAsync(model));
    }

    [Fact]
    public async Task Requests_are_recorded_in_order()
    {
        var model = new ScriptedModelProvider().Reply("one");

        await ReplyTextAsync(model);

        Assert.Same(Request, Assert.Single(model.Requests));
    }

    [Fact]
    public async Task Running_out_of_replies_fails_with_a_clear_message()
    {
        var model = new ScriptedModelProvider();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReplyTextAsync(model));

        Assert.Contains("no reply left", error.Message, StringComparison.Ordinal);
    }

    private static async Task<string> ReplyTextAsync(ScriptedModelProvider model)
    {
        var events = await model.StreamAsync(Request, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);
        return string.Concat(events.OfType<TextDelta>().Select(delta => delta.Text));
    }
}
